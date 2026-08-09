using Microsoft.Data.Sqlite;

namespace Resonance.Data;

public sealed class Database : IDisposable
{
    public const int CurrentSchemaVersion = 5;

    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);

    public Database(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
        Migrate();
    }

    private void Migrate()
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);
            INSERT INTO schema_version(version) SELECT 5 WHERE NOT EXISTS(SELECT 1 FROM schema_version);

            CREATE TABLE IF NOT EXISTS speaker(
              id INTEGER PRIMARY KEY,
              stable_key TEXT NOT NULL UNIQUE,
              npc_base_id INTEGER,
              display_name TEXT NOT NULL,
              gender INTEGER NULL,
              race INTEGER NULL,
              tribe INTEGER NULL,
              body INTEGER NULL,
              height INTEGER NULL,
              muscle_mass INTEGER NULL,
              model_chara_id INTEGER NULL,
              speaker_traits_json TEXT NULL,
              first_territory INTEGER NOT NULL,
              created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS speaker_alias(
              speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
              language TEXT NOT NULL,
              alias TEXT NOT NULL,
              UNIQUE(speaker_id, language, alias)
            );
            CREATE TABLE IF NOT EXISTS speaker_casting(
              speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
              domain_id TEXT NOT NULL,
              variant_traits_json TEXT,
              evidence_source TEXT NOT NULL,
              territory_id INTEGER NOT NULL,
              catalog_version INTEGER NOT NULL,
              is_stable INTEGER NOT NULL DEFAULT 1 CHECK(is_stable IN (0,1)),
              assigned_utc TEXT NOT NULL,
              PRIMARY KEY(speaker_id)
            );
            CREATE TABLE IF NOT EXISTS voice_profile(
              id TEXT PRIMARY KEY,
              kind INTEGER NOT NULL,
              language TEXT NOT NULL,
              model_hash TEXT NOT NULL,
              palette_version INTEGER,
              design_instruction TEXT,
              seed INTEGER,
              ref_text TEXT NOT NULL,
              speaker_embedding BLOB NOT NULL,
              rvq_codes BLOB NOT NULL,
              rvq_length INTEGER NOT NULL,
              codebooks INTEGER NOT NULL,
              domain_id TEXT NULL,
              catalog_version INTEGER NULL,
              traits_json TEXT NULL,
              variant_traits_json TEXT NULL,
              source_metadata TEXT,
              profile_hash TEXT NOT NULL UNIQUE,
              created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS speaker_voice(
              speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
              profile_id TEXT NOT NULL REFERENCES voice_profile(id) ON DELETE CASCADE,
              priority INTEGER NOT NULL,
              assigned_utc TEXT NOT NULL,
              PRIMARY KEY(speaker_id, profile_id)
            );
            CREATE TABLE IF NOT EXISTS pool_voice(
              domain_id TEXT NOT NULL,
              language TEXT NOT NULL,
              sex TEXT NOT NULL,
              profile_id TEXT NOT NULL UNIQUE REFERENCES voice_profile(id) ON DELETE CASCADE,
              assigned_speaker_id INTEGER REFERENCES speaker(id),
              state INTEGER NOT NULL CHECK(
                (state=0 AND assigned_speaker_id IS NULL)
                OR (state=1 AND assigned_speaker_id IS NOT NULL)
              ),
              slot_traits_json TEXT,
              template_id TEXT NOT NULL,
              sequence INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(domain_id, language, sex, profile_id)
            );
            CREATE TABLE IF NOT EXISTS pool_sequence(
              domain_id TEXT NOT NULL,
              language TEXT NOT NULL,
              sex TEXT NOT NULL,
              next_sequence INTEGER NOT NULL,
              PRIMARY KEY(domain_id, language, sex)
            );
            CREATE TABLE IF NOT EXISTS native_voice_observation(
              id INTEGER PRIMARY KEY,
              speaker_id INTEGER REFERENCES speaker(id),
              scd_path_hash TEXT NOT NULL,
              sound_number INTEGER NOT NULL,
              transcript_hash TEXT,
              observed_utc TEXT NOT NULL,
              UNIQUE(speaker_id, scd_path_hash, sound_number, transcript_hash)
            );
            CREATE TABLE IF NOT EXISTS official_reference_clip(
              id INTEGER PRIMARY KEY,
              speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
              source_hash TEXT NOT NULL,
              language TEXT NOT NULL,
              transcript TEXT NOT NULL,
              pcm_path TEXT,
              duration_seconds REAL NOT NULL,
              scd_path TEXT,
              sound_number INTEGER,
              source_origin TEXT NOT NULL DEFAULT 'legacy',
              source_priority INTEGER NOT NULL DEFAULT 0,
              catalog_version INTEGER,
              validated_utc TEXT,
              created_utc TEXT NOT NULL,
              UNIQUE(speaker_id, source_hash, language)
            );
            CREATE TABLE IF NOT EXISTS line_cache(
              cache_key TEXT PRIMARY KEY,
              profile_id TEXT NOT NULL REFERENCES voice_profile(id),
              normalized_text_hash TEXT NOT NULL,
              model_hash TEXT NOT NULL,
              audio_path TEXT NOT NULL,
              duration REAL NOT NULL,
              bytes INTEGER NOT NULL,
              last_used_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_speaker_voice_priority ON speaker_voice(speaker_id, priority DESC);
            CREATE INDEX IF NOT EXISTS ix_line_cache_lru ON line_cache(last_used_utc);
            """;
        command.ExecuteNonQuery();

        command.CommandText = "SELECT version FROM schema_version LIMIT 1";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version < 2)
        {
            command.CommandText = """
                CREATE TABLE official_reference_clip_v2(
                  id INTEGER PRIMARY KEY,
                  speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
                  source_hash TEXT NOT NULL,
                  language TEXT NOT NULL,
                  transcript TEXT NOT NULL,
                  pcm_path TEXT,
                  duration_seconds REAL NOT NULL,
                  created_utc TEXT NOT NULL,
                  UNIQUE(speaker_id, source_hash, language)
                );
                INSERT INTO official_reference_clip_v2(
                  id,speaker_id,source_hash,language,transcript,pcm_path,duration_seconds,created_utc)
                  SELECT id,speaker_id,source_hash,'und',transcript,pcm_path,duration_seconds,created_utc
                  FROM official_reference_clip;
                DROP TABLE official_reference_clip;
                ALTER TABLE official_reference_clip_v2 RENAME TO official_reference_clip;
                UPDATE schema_version SET version=2;
                """;
            command.ExecuteNonQuery();
        }
        if (version < 3)
        {
            command.CommandText = """
                CREATE TABLE native_voice_observation_v3(
                  id INTEGER PRIMARY KEY,
                  speaker_id INTEGER REFERENCES speaker(id),
                  scd_path_hash TEXT NOT NULL,
                  sound_number INTEGER NOT NULL,
                  transcript_hash TEXT,
                  observed_utc TEXT NOT NULL,
                  UNIQUE(speaker_id, scd_path_hash, sound_number, transcript_hash)
                );
                INSERT INTO native_voice_observation_v3
                  SELECT id,speaker_id,scd_path_hash,sound_number,transcript_hash,observed_utc
                  FROM native_voice_observation;
                DROP TABLE native_voice_observation;
                ALTER TABLE native_voice_observation_v3 RENAME TO native_voice_observation;
                UPDATE schema_version SET version=3;
                """;
            command.ExecuteNonQuery();
        }
        if (version < 4)
        {
            MigrateToV4(command);
        }
        EnsureOfficialReferenceClipV4(command);
        AddColumnIfMissing(command, "official_reference_clip", "scd_path", "TEXT");
        AddColumnIfMissing(command, "official_reference_clip", "sound_number", "INTEGER");
        AddColumnIfMissing(command, "official_reference_clip", "source_origin", "TEXT NOT NULL DEFAULT 'legacy'");
        AddColumnIfMissing(command, "official_reference_clip", "source_priority", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(command, "official_reference_clip", "catalog_version", "INTEGER");
        AddColumnIfMissing(command, "official_reference_clip", "validated_utc", "TEXT");
        if (version < 5)
        {
            command.CommandText = "UPDATE schema_version SET version=5";
            command.ExecuteNonQuery();
        }
        EnsureV4Columns(command);
        EnsureSpeakerCastingV4(command);
        EnsurePoolReadyInvariant(command);

        command.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_pool_ready
              ON pool_voice(domain_id, language, sex, state, assigned_speaker_id);
            CREATE INDEX IF NOT EXISTS ix_speaker_casting_domain
              ON speaker_casting(domain_id);
            CREATE INDEX IF NOT EXISTS ix_voice_profile_language
              ON voice_profile(language, kind, created_utc);
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void MigrateToV4(SqliteCommand command)
    {
        AddColumnIfMissing(command, "speaker", "height", "INTEGER NULL");
        AddColumnIfMissing(command, "speaker", "muscle_mass", "INTEGER NULL");
        AddColumnIfMissing(command, "speaker", "model_chara_id", "INTEGER NULL");
        AddColumnIfMissing(command, "speaker", "speaker_traits_json", "TEXT NULL");
        AddColumnIfMissing(command, "voice_profile", "domain_id", "TEXT NULL");
        AddColumnIfMissing(command, "voice_profile", "catalog_version", "INTEGER NULL");
        AddColumnIfMissing(command, "voice_profile", "traits_json", "TEXT NULL");
        AddColumnIfMissing(command, "voice_profile", "variant_traits_json", "TEXT NULL");

        command.CommandText = "DROP TABLE IF EXISTS protected_profile_ids; DROP TABLE IF EXISTS legacy_pool_profiles;";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE TEMP TABLE protected_profile_ids(profile_id TEXT PRIMARY KEY)";
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT OR IGNORE INTO protected_profile_ids(profile_id)
              SELECT profile_id FROM speaker_voice;
            INSERT OR IGNORE INTO protected_profile_ids(profile_id)
              SELECT profile_id FROM line_cache;
            """;
        command.ExecuteNonQuery();

        if (HasTable(command, "pool_voice"))
        {
            command.CommandText = """
                CREATE TEMP TABLE legacy_pool_profiles(
                  profile_id TEXT NOT NULL,
                  state INTEGER NOT NULL,
                  assigned_speaker_id INTEGER
                );
                INSERT INTO legacy_pool_profiles(profile_id,state,assigned_speaker_id)
                  SELECT profile_id,state,assigned_speaker_id FROM pool_voice;
                """;
            command.ExecuteNonQuery();
            command.CommandText = """
                DELETE FROM voice_profile
                WHERE id IN (
                  SELECT profile_id FROM legacy_pool_profiles
                  WHERE state=0 AND assigned_speaker_id IS NULL
                )
                AND id NOT IN (SELECT profile_id FROM protected_profile_ids);
                """;
            command.ExecuteNonQuery();
            command.CommandText = "DROP INDEX IF EXISTS ix_pool_ready; DROP TABLE pool_voice;";
            command.ExecuteNonQuery();
        }
        command.CommandText = "DROP TABLE IF EXISTS pool_sequence;";
        command.ExecuteNonQuery();
        command.CommandText = "DROP TABLE IF EXISTS protected_profile_ids; DROP TABLE IF EXISTS legacy_pool_profiles;";
        command.ExecuteNonQuery();
        command.CommandText = """
            CREATE TABLE pool_voice(
              domain_id TEXT NOT NULL,
              language TEXT NOT NULL,
              sex TEXT NOT NULL,
              profile_id TEXT NOT NULL UNIQUE REFERENCES voice_profile(id) ON DELETE CASCADE,
              assigned_speaker_id INTEGER REFERENCES speaker(id),
              state INTEGER NOT NULL CHECK(
                (state=0 AND assigned_speaker_id IS NULL)
                OR (state=1 AND assigned_speaker_id IS NOT NULL)
              ),
              slot_traits_json TEXT,
              template_id TEXT NOT NULL,
              sequence INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(domain_id, language, sex, profile_id)
            );
            CREATE TABLE pool_sequence(
              domain_id TEXT NOT NULL,
              language TEXT NOT NULL,
              sex TEXT NOT NULL,
              next_sequence INTEGER NOT NULL,
              PRIMARY KEY(domain_id, language, sex)
            );
            CREATE INDEX IF NOT EXISTS ix_pool_ready
              ON pool_voice(domain_id, language, sex, state, assigned_speaker_id);
            UPDATE schema_version SET version=4;
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureOfficialReferenceClipV4(SqliteCommand command)
    {
        if (HasOfficialReferenceClipV4Shape(command)) return;

        var hasLanguage = HasColumn(command, "official_reference_clip", "language");
        var languageExpression = hasLanguage
            ? "COALESCE(NULLIF(old.language,''),'und')"
            : "'und'";
        command.CommandText = "DROP TABLE IF EXISTS official_reference_clip_v4;";
        command.ExecuteNonQuery();
        command.CommandText = $"""
            CREATE TABLE official_reference_clip_v4(
              id INTEGER PRIMARY KEY,
              speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
              source_hash TEXT NOT NULL,
              language TEXT NOT NULL,
              transcript TEXT NOT NULL,
              pcm_path TEXT,
              duration_seconds REAL NOT NULL,
              created_utc TEXT NOT NULL,
              UNIQUE(speaker_id, source_hash, language)
            );
            INSERT INTO official_reference_clip_v4(
              id,speaker_id,source_hash,language,transcript,pcm_path,duration_seconds,created_utc)
            SELECT old.id,old.speaker_id,old.source_hash,{languageExpression},old.transcript,
                   old.pcm_path,old.duration_seconds,old.created_utc
            FROM official_reference_clip old;
            DROP TABLE official_reference_clip;
            ALTER TABLE official_reference_clip_v4 RENAME TO official_reference_clip;
            """;
        command.ExecuteNonQuery();
    }

    private static bool HasOfficialReferenceClipV4Shape(SqliteCommand command)
    {
        var languageNotNull = false;
        command.Parameters.Clear();
        command.CommandText = "PRAGMA table_info(official_reference_clip)";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "language", StringComparison.OrdinalIgnoreCase))
                {
                    languageNotNull = reader.GetInt32(3) != 0;
                    break;
                }
            }
        }
        if (!languageNotNull) return false;

        var uniqueIndexes = new List<string>();
        command.Parameters.Clear();
        command.CommandText = "PRAGMA index_list(official_reference_clip)";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetInt32(2) != 0) uniqueIndexes.Add(reader.GetString(1));
            }
        }
        if (uniqueIndexes.Count != 1) return false;

        var indexName = uniqueIndexes[0].Replace("\"", "\"\"");
        command.Parameters.Clear();
        command.CommandText = $"PRAGMA index_info(\"{indexName}\")";
        var columns = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) columns.Add(reader.GetString(2));
        }
        return columns.Count == 3
            && string.Equals(columns[0], "speaker_id", StringComparison.OrdinalIgnoreCase)
            && string.Equals(columns[1], "source_hash", StringComparison.OrdinalIgnoreCase)
            && string.Equals(columns[2], "language", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureV4Columns(SqliteCommand command)
    {
        AddColumnIfMissing(command, "speaker", "height", "INTEGER NULL");
        AddColumnIfMissing(command, "speaker", "muscle_mass", "INTEGER NULL");
        AddColumnIfMissing(command, "speaker", "model_chara_id", "INTEGER NULL");
        AddColumnIfMissing(command, "speaker", "speaker_traits_json", "TEXT NULL");
        AddColumnIfMissing(command, "voice_profile", "domain_id", "TEXT NULL");
        AddColumnIfMissing(command, "voice_profile", "catalog_version", "INTEGER NULL");
        AddColumnIfMissing(command, "voice_profile", "traits_json", "TEXT NULL");
        AddColumnIfMissing(command, "voice_profile", "variant_traits_json", "TEXT NULL");
    }

    private static void EnsureSpeakerCastingV4(SqliteCommand command)
    {
        if (!HasColumn(command, "speaker_casting", "language")) return;
        command.CommandText = "DROP TABLE IF EXISTS speaker_casting_v4;";
        command.ExecuteNonQuery();
        command.CommandText = """
            CREATE TABLE speaker_casting_v4(
              speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
              domain_id TEXT NOT NULL,
              variant_traits_json TEXT,
              evidence_source TEXT NOT NULL,
              territory_id INTEGER NOT NULL,
              catalog_version INTEGER NOT NULL,
              is_stable INTEGER NOT NULL DEFAULT 1 CHECK(is_stable IN (0,1)),
              assigned_utc TEXT NOT NULL,
              PRIMARY KEY(speaker_id)
            );
            INSERT INTO speaker_casting_v4(
              speaker_id,domain_id,variant_traits_json,evidence_source,
              territory_id,catalog_version,is_stable,assigned_utc)
            SELECT old.speaker_id,old.domain_id,old.variant_traits_json,old.evidence_source,
                   old.territory_id,old.catalog_version,old.is_stable,old.assigned_utc
            FROM speaker_casting old
            WHERE old.rowid=(
              SELECT candidate.rowid FROM speaker_casting candidate
              WHERE candidate.speaker_id=old.speaker_id
              ORDER BY candidate.is_stable DESC,candidate.assigned_utc DESC,candidate.language
              LIMIT 1
            );
            DROP TABLE speaker_casting;
            ALTER TABLE speaker_casting_v4 RENAME TO speaker_casting;
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsurePoolReadyInvariant(SqliteCommand command)
    {
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='pool_voice'";
        var sql = (string?)command.ExecuteScalar();
        if (sql?.Contains("state=0 AND assigned_speaker_id IS NULL", StringComparison.OrdinalIgnoreCase) == true
            && sql.Contains("state=1 AND assigned_speaker_id IS NOT NULL", StringComparison.OrdinalIgnoreCase)) return;

        command.CommandText = "DROP INDEX IF EXISTS ix_pool_ready; DROP TABLE IF EXISTS pool_voice_v4;";
        command.ExecuteNonQuery();
        command.CommandText = """
            CREATE TABLE pool_voice_v4(
              domain_id TEXT NOT NULL,
              language TEXT NOT NULL,
              sex TEXT NOT NULL,
              profile_id TEXT NOT NULL UNIQUE REFERENCES voice_profile(id) ON DELETE CASCADE,
              assigned_speaker_id INTEGER REFERENCES speaker(id),
              state INTEGER NOT NULL CHECK(
                (state=0 AND assigned_speaker_id IS NULL)
                OR (state=1 AND assigned_speaker_id IS NOT NULL)
              ),
              slot_traits_json TEXT,
              template_id TEXT NOT NULL,
              sequence INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(domain_id, language, sex, profile_id)
            );
            INSERT INTO pool_voice_v4(
              domain_id,language,sex,profile_id,state,assigned_speaker_id,
              slot_traits_json,template_id,sequence)
            SELECT pool_voice.domain_id,pool_voice.language,pool_voice.sex,pool_voice.profile_id,
                   pool_voice.state,pool_voice.assigned_speaker_id,
                   pool_voice.slot_traits_json,pool_voice.template_id,pool_voice.sequence
            FROM pool_voice
            JOIN voice_profile ON voice_profile.id=pool_voice.profile_id
            WHERE ((pool_voice.state=0 AND pool_voice.assigned_speaker_id IS NULL)
               OR (pool_voice.state=1 AND pool_voice.assigned_speaker_id IS NOT NULL
                   AND EXISTS(SELECT 1 FROM speaker WHERE id=pool_voice.assigned_speaker_id)))
              AND voice_profile.kind=1;
            DROP TABLE pool_voice;
            ALTER TABLE pool_voice_v4 RENAME TO pool_voice;
            """;
        command.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(SqliteCommand command, string table, string column, string definition)
    {
        if (HasColumn(command, table, column)) return;
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        command.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteCommand command, string table, string column)
    {
        command.Parameters.Clear();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool HasTable(SqliteCommand command, string table)
    {
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table)";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$table", table);
        var exists = Convert.ToInt32(command.ExecuteScalar()) != 0;
        command.Parameters.Clear();
        return exists;
    }

    public async Task<T> ReadAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await action(connection).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task WriteAsync(Func<SqliteConnection, Task> action, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await action(connection).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        connection.Dispose();
        gate.Dispose();
    }
}
