using System.Text.Json;
using Microsoft.Data.Sqlite;
using Resonance.Data;

namespace Resonance.Tts;

internal sealed class VoiceDataMigrations(
    Database database,
    int catalogVersion,
    Action<string, Exception?>? report = null,
    IReadOnlyList<VoiceDataMigrations.Step>? steps = null)
{
    private static readonly long[] OmegaModels = [327, 1892, 2320, 2321, 2323, 2328, 2387, 2389];
    internal const int OmegaMigrationId = 1;
    internal static readonly string OmegaModelsJson = JsonSerializer.Serialize(OmegaModels);
    private static readonly Step[] BuiltInSteps = [new(OmegaMigrationId, RecastOmega)];

    internal sealed record Step(int Id, Func<SqliteTransaction, string, int, CancellationToken, Outcome> Apply);
    internal readonly record struct Outcome(int Changed, bool Deferred = false);

    internal static bool OwnsOmegaRecast(long model, string domain) =>
        domain == "omicron" && OmegaModels.Contains(model);

    internal Task<int> RunAsync(string? modelHash, CancellationToken token) =>
        database.ReadAsync(connection =>
        {
            var models = new List<string>();
            if (modelHash is not null)
                models.Add(modelHash);
            else
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT DISTINCT model_hash FROM voice_profile ORDER BY model_hash";
                using var reader = command.ExecuteReader();
                while (reader.Read()) models.Add(reader.GetString(0));
            }

            var changed = 0;
            foreach (var model in models)
                changed += RunModel(connection, model, token);
            return Task.FromResult(changed);
        }, token);

    private int RunModel(SqliteConnection connection, string model, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int? migrationId = null;
        int? deferredId = null;
        var applied = new List<int>();
        var changed = 0;
        try
        {
            using var transaction = connection.BeginTransaction();
            using var lookup = Command(transaction, """
                SELECT EXISTS(SELECT 1 FROM voice_data_migration WHERE migration_id=$id AND model_hash=$model)
                """, ("$model", model), ("$id", 0));
            using var record = Command(transaction, """
                INSERT INTO voice_data_migration(migration_id,model_hash,applied_utc) VALUES($id,$model,$utc)
                """, ("$model", model), ("$id", 0), ("$utc", DateTimeOffset.UtcNow.ToString("O")));
            foreach (var step in (steps ?? BuiltInSteps).OrderBy(step => step.Id))
            {
                token.ThrowIfCancellationRequested();
                migrationId = step.Id;
                lookup.Parameters["$id"].Value = step.Id;
                if (Convert.ToInt32(lookup.ExecuteScalar()) != 0) continue;
                var outcome = step.Apply(transaction, model, catalogVersion, token);
                changed += outcome.Changed;
                if (outcome.Deferred)
                {
                    deferredId = step.Id;
                    break;
                }
                record.Parameters["$id"].Value = step.Id;
                record.ExecuteNonQuery();
                applied.Add(step.Id);
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            report?.Invoke($"Voice data migration {migrationId:D3} rolled back for model {model}", error);
            return 0;
        }

        if (applied.Count > 0)
            report?.Invoke($"Voice data migrations {String.Join(", ", applied)} applied for model {model}", null);
        if (deferredId is { } pending)
            report?.Invoke($"Voice data migration {pending:D3} deferred for model {model}: compatible voice unavailable", null);
        return changed;
    }

    private static Outcome RecastOmega(
        SqliteTransaction transaction, string model, int catalogVersion, CancellationToken token)
    {
        var bank = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = Command(transaction, """
            SELECT p.language,p.id
            FROM speaker f JOIN speaker_voice sv ON sv.speaker_id=f.id
            JOIN voice_profile p ON p.id=sv.profile_id
            WHERE f.stable_key=$fallback AND p.model_hash=$model AND p.kind=$designed AND p.domain_id='omicron'
            ORDER BY sv.priority DESC,p.created_utc DESC,p.id
            """, ("$fallback", VoiceRegistry.DomainFallbackSpeakerKey("omicron")),
            ("$model", model), ("$designed", (int)VoiceProfileKind.Designed)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) bank.TryAdd(reader.GetString(0), reader.GetString(1));
        }

        var assignments = new List<Assignment>();
        using (var command = Command(transaction, """
            SELECT s.id,p.language,p.id,
              p.kind=$designed
              AND COALESCE(CASE WHEN json_valid(p.source_metadata)
                THEN json_extract(p.source_metadata,'$.promptOverride') END,0)=0
              AND (EXISTS(SELECT 1 FROM pool_voice pv WHERE pv.profile_id=p.id AND pv.assigned_speaker_id=s.id)
                OR EXISTS(SELECT 1 FROM speaker_voice fv JOIN speaker f ON f.id=fv.speaker_id
                  WHERE fv.profile_id=p.id AND f.stable_key LIKE 'fallback:%'))
            FROM speaker s
            LEFT JOIN speaker_casting c ON c.speaker_id=s.id
            LEFT JOIN (
              speaker_voice sv JOIN voice_profile p ON p.id=sv.profile_id AND p.model_hash=$model
            ) ON sv.speaker_id=s.id
            WHERE s.model_chara_id IN (SELECT value FROM json_each($models))
              AND COALESCE(s.race,0)=0 AND (s.stable_key LIKE 'npc:%' OR s.stable_key LIKE 'bnpc:%')
              AND (p.id IS NOT NULL OR (c.domain_id<>'omicron'
                AND NOT EXISTS(SELECT 1 FROM speaker_voice link WHERE link.speaker_id=s.id)))
            """, ("$model", model), ("$models", OmegaModelsJson),
            ("$designed", (int)VoiceProfileKind.Designed)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                assignments.Add(reader.IsDBNull(2)
                    ? new(reader.GetInt64(0), null, null, false)
                    : new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        }

        var changed = 0;
        var deferred = false;
        var utc = DateTimeOffset.UtcNow.ToString("O");
        foreach (var speaker in assignments.GroupBy(row => row.Speaker))
        {
            token.ThrowIfCancellationRequested();
            var castReady = false;
            var swaps = 0;
            foreach (var language in speaker.GroupBy(row => row.Language))
            {
                if (language.Key is null)
                {
                    castReady = bank.Count > 0;
                    deferred |= !castReady;
                    continue;
                }
                if (language.Any(row => !row.Automatic)) continue;
                if (!bank.TryGetValue(language.Key, out var replacement))
                {
                    deferred = true;
                    continue;
                }
                castReady = true;
                var stale = language.Select(row => row.Profile).Where(profile => profile != replacement).ToArray();
                if (stale.Length == 0) continue;
                using var swap = Command(transaction, """
                    DELETE FROM speaker_voice WHERE speaker_id=$speaker
                      AND profile_id IN (SELECT value FROM json_each($stale));
                    DELETE FROM pool_voice WHERE assigned_speaker_id=$speaker
                      AND profile_id IN (SELECT value FROM json_each($stale));
                    INSERT INTO speaker_voice(speaker_id,profile_id,priority,assigned_utc)
                    VALUES($speaker,$replacement,100,$utc) ON CONFLICT(speaker_id,profile_id) DO NOTHING;
                    """, ("$speaker", speaker.Key), ("$stale", JsonSerializer.Serialize(stale)),
                    ("$replacement", replacement), ("$utc", utc));
                swap.ExecuteNonQuery();
                swaps++;
            }
            if (!castReady) continue;
            using var cast = Command(transaction, """
                INSERT INTO speaker_casting(speaker_id,domain_id,variant_traits_json,evidence_source,
                    territory_id,catalog_version,is_stable,assigned_utc)
                SELECT id,'omicron','{}','Species',first_territory,$catalog,1,$utc FROM speaker WHERE id=$speaker
                ON CONFLICT(speaker_id) DO UPDATE SET domain_id=excluded.domain_id,
                    variant_traits_json=excluded.variant_traits_json,evidence_source=excluded.evidence_source,
                    catalog_version=excluded.catalog_version,is_stable=1,assigned_utc=excluded.assigned_utc
                WHERE speaker_casting.domain_id<>excluded.domain_id
                """, ("$speaker", speaker.Key), ("$catalog", catalogVersion), ("$utc", utc));
            changed += Math.Max(swaps, cast.ExecuteNonQuery());
        }
        return new(changed, deferred);
    }

    private static SqliteCommand Command(
        SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private readonly record struct Assignment(long Speaker, string? Language, string? Profile, bool Automatic);
}
