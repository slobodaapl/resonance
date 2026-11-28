using System.Text.Json;
using Microsoft.Data.Sqlite;
using Resonance.Bootstrap;
using Resonance.Data;
using Resonance.Game;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed partial class OfficialProfilePackManagerTests
{
    [Fact]
    public async Task StartupMigratesInstalledModelsAndPreservesExplicitVoices()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "voice-migration-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "user.sqlite3");
        try
        {
            long omegaId, silentId, officialId, customId;
            StoredVoiceProfile english, japanese, otherModel, official, custom, old;
            using (var before = new Database(path))
            {
                var voices = new VoiceRegistry(before);
                var omega = await SavedOmega(voices, "npc:1021680", 1892, token);
                omegaId = omega.Id;
                silentId = (await SavedOmega(voices, "npc:1026371", 327, token)).Id;
                officialId = (await SavedOmega(voices, "npc:1026372", 2387, token)).Id;
                customId = (await SavedOmega(voices, "npc:1026373", 2323, token)).Id;
                old = await SeedFallback(voices, "ala_mhigan", "masculine_adult", "english", "q4", token);
                await voices.SaveAndAssignAsync(omegaId, old, token);
                await voices.SaveAndAssignAsync(omegaId,
                    await SeedFallback(voices, "generic_world", "masculine_adult", "english", "q4", token), token);
                await voices.SaveAndAssignAsync(omegaId,
                    await SeedFallback(voices, "ala_mhigan", "masculine_adult", "japanese", "q4", token), token);
                await voices.SaveAndAssignAsync(omegaId,
                    await SeedFallback(voices, "ala_mhigan", "masculine_adult", "english", "q8", token), token);
                english = await SeedFallback(voices, "omicron", "default", "english", "q4", token);
                japanese = await SeedFallback(voices, "omicron", "default", "japanese", "q4", token);
                otherModel = await SeedFallback(voices, "omicron", "default", "english", "q8", token);
                official = await voices.SaveAndAssignAsync(officialId, VoiceRegistry.CreateProfile(
                    VoiceProfileKind.Official, "english", "q4", null, null, null,
                    new VoiceReference([1], [2], 1, 1, "official")), token);
                custom = await voices.SaveAndAssignAsync(customId, VoiceRegistry.CreateProfile(
                    VoiceProfileKind.Designed, "english", "q4", null, "custom", null,
                    new VoiceReference([1], [3], 1, 1, "custom"),
                    """{"promptOverride":true}""", "ala_mhigan", 5, "{}"), token);
                await before.WriteAsync(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        DROP TABLE voice_data_migration;
                        INSERT INTO line_cache VALUES('cached-omega',$profile,'transcript','q4','cached.wav',1,1,'before');
                        """;
                    command.Parameters.AddWithValue("$profile", old.Id);
                    command.ExecuteNonQuery();
                    return Task.CompletedTask;
                }, token);
            }

            using var after = new Database(path);
            var registry = new VoiceRegistry(after);
            using var manager = MigrationManager(root, after, registry);
            Assert.Equal(4, await manager.MigrateInstalledVoicesAsync(token));
            Assert.Equal(english.Id, (await registry.GetBestVoiceAsync(omegaId, "english", "q4", token))?.Id);
            Assert.Equal(japanese.Id, (await registry.GetBestVoiceAsync(omegaId, "japanese", "q4", token))?.Id);
            Assert.Equal(otherModel.Id, (await registry.GetBestVoiceAsync(omegaId, "english", "q8", token))?.Id);
            Assert.Equal("omicron", (await registry.GetSpeakerCastingAsync(omegaId, token))?.DomainId);
            Assert.Equal("omicron", (await registry.GetSpeakerCastingAsync(silentId, token))?.DomainId);
            Assert.Equal(official.Id, (await registry.GetBestVoiceAsync(officialId, "english", "q4", token))?.Id);
            Assert.Equal(custom.Id, (await registry.GetBestVoiceAsync(customId, "english", "q4", token))?.Id);
            Assert.Equal("ala_mhigan", (await registry.GetSpeakerCastingAsync(customId, token))?.DomainId);
            Assert.Equal(old.Id, await Scalar(after, "SELECT profile_id FROM line_cache WHERE cache_key='cached-omega'", token));
            Assert.Equal(6L, await Scalar(after, "SELECT version FROM schema_version", token));
            Assert.Equal(2L, await Scalar(after, "SELECT COUNT(*) FROM voice_data_migration", token));
            Assert.Equal(0, await manager.MigrateInstalledVoicesAsync(token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CachedImportRetriesDeferredOmegaAndRollsBackWhenCompletionCannotBeRecorded()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "voice-migration-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "official-profile-pack");
        Directory.CreateDirectory(directory);
        try
        {
            const string model = "q4";
            var pack = Path.Combine(directory, "official-profiles.sqlite3");
            CreatePackDatabase(pack, 1024, model);
            using (var source = new SqliteConnection($"Data Source={pack};Pooling=False"))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = """
                    DELETE FROM official_profile;
                    UPDATE pack_metadata SET schema_version=2,pack_version=20,catalog_version=5;
                    UPDATE fallback_profile SET domain_id='omicron',profile_hash=$hash;
                    CREATE TABLE scene_node(cutscene_id INTEGER,language TEXT,node_id TEXT);
                    CREATE TABLE scene_edge(cutscene_id INTEGER,language TEXT,current_node_id TEXT,next_node_id TEXT);
                    INSERT INTO scene_node VALUES(1,'english','intro');
                    INSERT INTO scene_edge VALUES(1,'english',NULL,'intro');
                    """;
                var profile = VoiceRegistry.CreateProfile(VoiceProfileKind.Designed, "english", model,
                    null, null, null, new VoiceReference(Enumerable.Repeat(0.1f, 1024).ToArray(),
                        new int[16 * 187], 187, 16, "Reference"), "{}", "omicron", 5, "{}");
                command.Parameters.AddWithValue("$hash", profile.ProfileHash);
                command.ExecuteNonQuery();
            }
            var bankBytes = await File.ReadAllBytesAsync(pack, token);
            await File.WriteAllTextAsync(Path.Combine(directory, "official-profile-pack.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = 2, PackVersion = 20, CatalogVersion = 5,
                Url = "https://example.invalid/pack.zip", Length = 1, Sha256 = new string('a', 64),
                DatabaseSha256 = Hash(bankBytes),
            }), token);

            using var database = new Database(Path.Combine(root, "user.sqlite3"));
            var voices = new VoiceRegistry(database);
            var omega = await SavedOmega(voices, "npc:1021680", 1892, token);
            var old = await SeedFallback(voices, "ala_mhigan", "masculine_adult", "english", model, token);
            await voices.SaveAndAssignAsync(omega.Id, old, token);
            var other = await SavedOmega(voices, "npc:1026371", 327, token);
            await voices.SaveAndAssignAsync(other.Id, old, token);
            await voices.SaveAndAssignAsync(other.Id,
                await SeedFallback(voices, "ala_mhigan", "masculine_adult", "japanese", model, token), token);
            var japanese = await SeedFallback(voices, "omicron", "default", "japanese", model, token);
            var errors = new List<Exception>();
            using var manager = MigrationManager(root, database, voices, (_, error) =>
            {
                if (error is not null) errors.Add(error);
            });
            Assert.Equal(1, await manager.MigrateInstalledVoicesAsync(token));
            Assert.Equal(0L, await Scalar(database, "SELECT COUNT(*) FROM voice_data_migration", token));
            Assert.Equal(old.Id, (await voices.GetBestVoiceAsync(omega.Id, "english", model, token))?.Id);
            Assert.Equal(japanese.Id, (await voices.GetBestVoiceAsync(other.Id, "japanese", model, token))?.Id);

            await Scalar(database, """
                CREATE TRIGGER fail_migration BEFORE INSERT ON voice_data_migration
                BEGIN SELECT RAISE(ABORT,'migration ledger rejected'); END;
                """, token);
            var failed = await manager.SynchronizeAsync(model, token, allowRemoteUpdate: false);
            Assert.Equal(1, failed.ImportedProfiles);
            Assert.Equal(0, failed.ReconciledAssignments);
            Assert.IsType<SqliteException>(Assert.Single(errors));
            Assert.Equal(old.Id, (await voices.GetBestVoiceAsync(omega.Id, "english", model, token))?.Id);
            Assert.Equal("ala_mhigan", (await voices.GetSpeakerCastingAsync(omega.Id, token))?.DomainId);
            Assert.Equal(old.Id, (await voices.GetBestVoiceAsync(other.Id, "english", model, token))?.Id);
            Assert.Equal(0L, await Scalar(database, "SELECT COUNT(*) FROM voice_data_migration", token));

            await Scalar(database, "DROP TRIGGER fail_migration", token);
            var retry = await manager.SynchronizeAsync(model, token, allowRemoteUpdate: false);
            Assert.Equal(0, retry.ImportedProfiles);
            Assert.Equal(2, retry.ReconciledAssignments);
            var replacement = await voices.GetFallbackVoiceAsync("omicron", "default", "english", model, token);
            Assert.NotNull(replacement);
            Assert.Equal(replacement.Id, (await voices.GetBestVoiceAsync(omega.Id, "english", model, token))?.Id);
            Assert.Equal(replacement.Id, (await voices.GetBestVoiceAsync(other.Id, "english", model, token))?.Id);
            Assert.Equal(0, (await manager.SynchronizeAsync(model, token, allowRemoteUpdate: false)).ReconciledAssignments);
            Assert.Equal(bankBytes, await File.ReadAllBytesAsync(pack, token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LaterMigrationCopiesExistingDataAfterEarlierStepCompletes()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "voice-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var database = new Database(Path.Combine(root, "user.sqlite3"));
            var voices = new VoiceRegistry(database);
            await SavedOmega(voices, "npc:1021680", 1892, token);
            var ready = false;
            var runner = new VoiceDataMigrations(database, 5, steps:
            [
                new(2, (transaction, _, _, _) => Update(transaction, """
                    INSERT INTO speaker_alias(speaker_id,language,alias)
                    SELECT id,'japanese',display_name FROM speaker WHERE display_name='Updated Omega'
                    """)),
                new(1, (transaction, _, _, _) => ready
                    ? Update(transaction, "UPDATE speaker SET display_name='Updated Omega' WHERE stable_key='npc:1021680'")
                    : new(0, Deferred: true)),
            ]);

            Assert.Equal(0, await runner.RunAsync("q4", token));
            Assert.Null(await Scalar(database, "SELECT alias FROM speaker_alias WHERE language='japanese'", token));
            ready = true;
            Assert.Equal(2, await runner.RunAsync("q4", token));
            Assert.Equal("Updated Omega", await Scalar(database, "SELECT alias FROM speaker_alias WHERE language='japanese'", token));
            Assert.Equal(0, await runner.RunAsync("q4", token));
        }
        finally { Directory.Delete(root, true); }

        static VoiceDataMigrations.Outcome Update(SqliteTransaction transaction, string sql)
        {
            using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return new(command.ExecuteNonQuery());
        }
    }

    private static OfficialProfilePackManager MigrationManager(
        string root, Database database, VoiceRegistry voices, Action<string, Exception?>? report = null) =>
        new(root, database, voices, OfficialVoiceCatalog.Load(ProjectPath("assets", "official-voices.json")),
            CastingCatalog(), reportMigration: report);

    private static async Task<SpeakerIdentity> SavedOmega(
        VoiceRegistry voices, string key, long model, CancellationToken token)
    {
        var speaker = await voices.ResolveSpeakerAsync(key, null, "Omega", 612, "english",
            new SpeakerMetadata(Race: 0, ModelCharaId: model), token);
        await voices.SaveSpeakerCastingAsync(speaker.Id, "ala_mhigan", "{}", "Territory", 612, 5, true, token);
        return speaker;
    }

    private static async Task<StoredVoiceProfile> SeedFallback(
        VoiceRegistry voices, string domain, string variant, string language, string model, CancellationToken token)
    {
        var bank = await voices.ResolveSpeakerAsync(VoiceRegistry.DomainFallbackSpeakerKey(domain, variant),
            null, domain, 0, language, token);
        return await voices.SaveAndAssignAsync(bank.Id, VoiceRegistry.CreateProfile(
            VoiceProfileKind.Designed, language, model, null, null, null,
            new VoiceReference([1], [1], 1, 1, domain), "{}", domain, 5, "{}"), token);
    }

    private static Task<object?> Scalar(Database database, string sql, CancellationToken token) =>
        database.ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Task.FromResult(command.ExecuteScalar());
        }, token);
}
