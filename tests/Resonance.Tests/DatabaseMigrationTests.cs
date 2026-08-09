using Microsoft.Data.Sqlite;
using Resonance.Data;

namespace Resonance.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public async Task VersionOneOfficialSourcesMigrateToPerSpeakerUniqueness()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "voices.sqlite3");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA foreign_keys=ON;
                    CREATE TABLE schema_version(version INTEGER NOT NULL);
                    INSERT INTO schema_version VALUES(1);
                    CREATE TABLE speaker(
                      id INTEGER PRIMARY KEY,stable_key TEXT NOT NULL UNIQUE,npc_base_id INTEGER,
                      display_name TEXT NOT NULL,gender INTEGER,race INTEGER,tribe INTEGER,body INTEGER,
                      first_territory INTEGER NOT NULL,created_utc TEXT NOT NULL);
                    INSERT INTO speaker(id,stable_key,display_name,first_territory,created_utc)
                      VALUES(1,'npc:1','One',1,'now'),(2,'npc:2','Two',1,'now');
                    CREATE TABLE official_reference_clip(
                      id INTEGER PRIMARY KEY,speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
                      source_hash TEXT NOT NULL UNIQUE,transcript TEXT NOT NULL,pcm_path TEXT,
                      duration_seconds REAL NOT NULL,created_utc TEXT NOT NULL);
                    INSERT INTO official_reference_clip(speaker_id,source_hash,transcript,duration_seconds,created_utc)
                      VALUES(1,'shared','line',1.0,'now');
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            using var database = new Database(path);
            await database.WriteAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO official_reference_clip(
                      speaker_id,source_hash,language,transcript,duration_seconds,created_utc)
                    VALUES(2,'shared','und','other line',1.0,'now');
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);
            var result = await database.ReadAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT (SELECT version FROM schema_version),
                           COUNT(*),SUM(CASE WHEN language='und' THEN 1 ELSE 0 END)
                    FROM official_reference_clip WHERE source_hash='shared'
                    """;
                await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
                return (
                    Version: reader.GetInt32(0),
                    Count: reader.GetInt32(1),
                    UnknownLanguageCount: reader.GetInt32(2));
            }, TestContext.Current.CancellationToken);

            Assert.Equal(4, result.Version);
            Assert.Equal(2, result.Count);
            Assert.Equal(2, result.UnknownLanguageCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OfficialReferenceLanguageIsPartOfThePerSpeakerSourceKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-reference-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "voices.sqlite3");
        try
        {
            using (var database = new Database(path))
            {
                await database.WriteAsync(async connection =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO speaker(stable_key,display_name,first_territory,created_utc)
                        VALUES('npc:reference-language','Reference NPC',1,'now');
                        INSERT INTO official_reference_clip(
                          speaker_id,source_hash,language,transcript,duration_seconds,created_utc)
                        VALUES
                          ((SELECT id FROM speaker WHERE stable_key='npc:reference-language'),
                            'shared','en','English line',1.0,'now'),
                          ((SELECT id FROM speaker WHERE stable_key='npc:reference-language'),
                            'shared','ja','Japanese line',1.0,'now');
                        """;
                    await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }, TestContext.Current.CancellationToken);

                await Assert.ThrowsAsync<SqliteException>(() => database.WriteAsync(async connection =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO official_reference_clip(
                          speaker_id,source_hash,language,transcript,duration_seconds,created_utc)
                        SELECT id,'shared','en','Duplicate English line',1.0,'now'
                        FROM speaker WHERE stable_key='npc:reference-language';
                        """;
                    await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }, TestContext.Current.CancellationToken));
            }

            using var reopened = new Database(path);
            var languages = await reopened.ReadAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*),
                           SUM(CASE WHEN language='en' THEN 1 ELSE 0 END),
                           SUM(CASE WHEN language='ja' THEN 1 ELSE 0 END)
                    FROM official_reference_clip
                    WHERE speaker_id=(SELECT id FROM speaker WHERE stable_key='npc:reference-language')
                      AND source_hash='shared'
                    """;
                await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
                return (Total: reader.GetInt32(0), English: reader.GetInt32(1), Japanese: reader.GetInt32(2));
            }, TestContext.Current.CancellationToken);

            Assert.Equal(2, languages.Total);
            Assert.Equal(1, languages.English);
            Assert.Equal(1, languages.Japanese);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task VersionThreePoolMigrationRemovesOnlyUnprotectedReadyProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-v3-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "voices.sqlite3");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA foreign_keys=ON;
                    CREATE TABLE schema_version(version INTEGER NOT NULL);
                    INSERT INTO schema_version VALUES(3);
                    CREATE TABLE speaker(
                      id INTEGER PRIMARY KEY,stable_key TEXT NOT NULL UNIQUE,npc_base_id INTEGER,
                      display_name TEXT NOT NULL,gender INTEGER,race INTEGER,tribe INTEGER,body INTEGER,
                      first_territory INTEGER NOT NULL,created_utc TEXT NOT NULL);
                    INSERT INTO speaker(id,stable_key,display_name,first_territory,created_utc)
                      VALUES(1,'npc:assigned','Assigned',1,'now');
                    CREATE TABLE speaker_alias(
                      speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
                      language TEXT NOT NULL,alias TEXT NOT NULL,
                      UNIQUE(speaker_id,language,alias));
                    CREATE TABLE speaker_casting(
                      speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
                      language TEXT NOT NULL,domain_id TEXT NOT NULL,variant_traits_json TEXT,
                      evidence_source TEXT NOT NULL,territory_id INTEGER NOT NULL,
                      catalog_version INTEGER NOT NULL,is_stable INTEGER NOT NULL,assigned_utc TEXT NOT NULL,
                      PRIMARY KEY(speaker_id,language));
                    INSERT INTO speaker_casting(
                      speaker_id,language,domain_id,evidence_source,territory_id,catalog_version,is_stable,assigned_utc)
                      VALUES(1,'english','ishgardian','identity',1,1,1,'2024-01-01'),
                            (1,'japanese','thavnairian','catalog',2,2,0,'2024-01-02');
                    CREATE TABLE voice_profile(
                      id TEXT PRIMARY KEY,kind INTEGER NOT NULL,language TEXT NOT NULL,
                      model_hash TEXT NOT NULL,palette_version INTEGER,design_instruction TEXT,
                      seed INTEGER,ref_text TEXT NOT NULL,speaker_embedding BLOB NOT NULL,
                      rvq_codes BLOB NOT NULL,rvq_length INTEGER NOT NULL,codebooks INTEGER NOT NULL,
                      source_metadata TEXT,profile_hash TEXT NOT NULL UNIQUE,created_utc TEXT NOT NULL);
                    INSERT INTO voice_profile(
                      id,kind,language,model_hash,ref_text,speaker_embedding,rvq_codes,rvq_length,
                      codebooks,profile_hash,created_utc)
                    VALUES
                      ('ready',1,'english','m', 'ready',X'00000000',X'00000000',1,1,'hash-ready','now'),
                      ('assigned',1,'english','m', 'assigned',X'00000000',X'00000000',1,1,'hash-assigned','now'),
                      ('cached',1,'english','m', 'cached',X'00000000',X'00000000',1,1,'hash-cached','now');
                    CREATE TABLE speaker_voice(
                      speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
                      profile_id TEXT NOT NULL REFERENCES voice_profile(id) ON DELETE CASCADE,
                      priority INTEGER NOT NULL,assigned_utc TEXT NOT NULL,
                      PRIMARY KEY(speaker_id,profile_id));
                    INSERT INTO speaker_voice(speaker_id,profile_id,priority,assigned_utc)
                      VALUES(1,'assigned',100,'now');
                    CREATE TABLE pool_voice(
                      territory_id INTEGER NOT NULL,language TEXT NOT NULL,archetype TEXT NOT NULL,
                      profile_id TEXT NOT NULL UNIQUE REFERENCES voice_profile(id) ON DELETE CASCADE,
                      state INTEGER NOT NULL,assigned_speaker_id INTEGER REFERENCES speaker(id),
                      PRIMARY KEY(territory_id,language,archetype,profile_id));
                    INSERT INTO pool_voice(territory_id,language,archetype,profile_id,state,assigned_speaker_id)
                      VALUES(10,'english','feminine_adult','ready',0,NULL),
                            (10,'english','feminine_adult','assigned',1,1),
                            (10,'english','feminine_adult','cached',0,NULL);
                    CREATE TABLE pool_sequence(
                      territory_id INTEGER NOT NULL,language TEXT NOT NULL,archetype TEXT NOT NULL,
                      next_sequence INTEGER NOT NULL,
                      PRIMARY KEY(territory_id,language,archetype));
                    CREATE TABLE native_voice_observation(
                      id INTEGER PRIMARY KEY,speaker_id INTEGER REFERENCES speaker(id),
                      scd_path_hash TEXT NOT NULL,sound_number INTEGER NOT NULL,transcript_hash TEXT,
                      observed_utc TEXT NOT NULL,
                      UNIQUE(speaker_id,scd_path_hash,sound_number,transcript_hash));
                    CREATE TABLE official_reference_clip(
                      id INTEGER PRIMARY KEY,speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
                      source_hash TEXT NOT NULL,transcript TEXT NOT NULL,pcm_path TEXT,
                      duration_seconds REAL NOT NULL,created_utc TEXT NOT NULL,
                      UNIQUE(speaker_id,source_hash));
                    CREATE TABLE line_cache(
                      cache_key TEXT PRIMARY KEY,profile_id TEXT NOT NULL REFERENCES voice_profile(id),
                      normalized_text_hash TEXT NOT NULL,model_hash TEXT NOT NULL,audio_path TEXT NOT NULL,
                      duration REAL NOT NULL,bytes INTEGER NOT NULL,last_used_utc TEXT NOT NULL);
                    INSERT INTO line_cache(cache_key,profile_id,normalized_text_hash,model_hash,audio_path,duration,bytes,last_used_utc)
                      VALUES('cached-line','cached','text','m','cached.wav',1.0,1,'now');
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            using (var database = new Database(path))
            {
                var state = await database.ReadAsync(async connection =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT
                          (SELECT version FROM schema_version),
                          (SELECT COUNT(*) FROM voice_profile WHERE id='ready'),
                          (SELECT COUNT(*) FROM voice_profile WHERE id='assigned'),
                          (SELECT COUNT(*) FROM voice_profile WHERE id='cached'),
                          (SELECT COUNT(*) FROM line_cache WHERE profile_id='cached'),
                          (SELECT COUNT(*) FROM pool_voice WHERE domain_id='legacy:10'),
                          (SELECT COUNT(*) FROM speaker_casting),
                          (SELECT domain_id FROM speaker_casting WHERE speaker_id=1)
                        """;
                    await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                    Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
                    return (
                        Version: reader.GetInt32(0),
                        Ready: reader.GetInt32(1),
                        Assigned: reader.GetInt32(2),
                        Cached: reader.GetInt32(3),
                        CacheReference: reader.GetInt32(4),
                        PoolRows: reader.GetInt32(5),
                        CastingRows: reader.GetInt32(6),
                        CastingDomain: reader.GetString(7));
                }, TestContext.Current.CancellationToken);
                Assert.Equal(4, state.Version);
                Assert.Equal(0, state.Ready);
                Assert.Equal(1, state.Assigned);
                Assert.Equal(1, state.Cached);
                Assert.Equal(1, state.CacheReference);
                Assert.Equal(0, state.PoolRows);
                Assert.Equal(1, state.CastingRows);
                Assert.Equal("ishgardian", state.CastingDomain);
            }

            using var reopened = new Database(path);
            var version = await reopened.ReadAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT version FROM schema_version";
                return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }, TestContext.Current.CancellationToken);
            Assert.Equal(4, version);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InterimV4PoolRepairDropsOfficialMembershipButPreservesReferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-interim-v4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "voices.sqlite3");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA foreign_keys=ON;
                    CREATE TABLE schema_version(version INTEGER NOT NULL);
                    INSERT INTO schema_version VALUES(4);
                    CREATE TABLE speaker(
                      id INTEGER PRIMARY KEY,stable_key TEXT NOT NULL UNIQUE,npc_base_id INTEGER,
                      display_name TEXT NOT NULL,gender INTEGER,race INTEGER,tribe INTEGER,body INTEGER,
                      first_territory INTEGER NOT NULL,created_utc TEXT NOT NULL);
                    INSERT INTO speaker(id,stable_key,display_name,first_territory,created_utc)
                      VALUES(1,'npc:interim','Interim NPC',1,'now');
                    CREATE TABLE official_reference_clip(
                      id INTEGER PRIMARY KEY,speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
                      source_hash TEXT NOT NULL,transcript TEXT NOT NULL,pcm_path TEXT,
                      duration_seconds REAL NOT NULL,created_utc TEXT NOT NULL,
                      UNIQUE(speaker_id,source_hash));
                    INSERT INTO official_reference_clip(
                      speaker_id,source_hash,transcript,duration_seconds,created_utc)
                      VALUES(1,'interim-source','Interim line',1.0,'now');
                    CREATE TABLE voice_profile(
                      id TEXT PRIMARY KEY,kind INTEGER NOT NULL,language TEXT NOT NULL,
                      model_hash TEXT NOT NULL,palette_version INTEGER,design_instruction TEXT,
                      seed INTEGER,ref_text TEXT NOT NULL,speaker_embedding BLOB NOT NULL,
                      rvq_codes BLOB NOT NULL,rvq_length INTEGER NOT NULL,codebooks INTEGER NOT NULL,
                      domain_id TEXT,catalog_version INTEGER,traits_json TEXT,variant_traits_json TEXT,
                      source_metadata TEXT,profile_hash TEXT NOT NULL UNIQUE,created_utc TEXT NOT NULL);
                    INSERT INTO voice_profile(
                      id,kind,language,model_hash,ref_text,speaker_embedding,rvq_codes,rvq_length,
                      codebooks,profile_hash,created_utc)
                      VALUES
                        ('official-interim',2,'english','m','official',X'00000000',X'00000000',1,1,'hash-official','now'),
                        ('designed-interim',1,'english','m','designed',X'00000000',X'00000000',1,1,'hash-designed','now');
                    CREATE TABLE speaker_voice(
                      speaker_id INTEGER NOT NULL REFERENCES speaker(id) ON DELETE CASCADE,
                      profile_id TEXT NOT NULL REFERENCES voice_profile(id) ON DELETE CASCADE,
                      priority INTEGER NOT NULL,assigned_utc TEXT NOT NULL,
                      PRIMARY KEY(speaker_id,profile_id));
                    INSERT INTO speaker_voice(speaker_id,profile_id,priority,assigned_utc)
                      VALUES(1,'official-interim',200,'now');
                    CREATE TABLE line_cache(
                      cache_key TEXT PRIMARY KEY,profile_id TEXT NOT NULL REFERENCES voice_profile(id),
                      normalized_text_hash TEXT NOT NULL,model_hash TEXT NOT NULL,audio_path TEXT NOT NULL,
                      duration REAL NOT NULL,bytes INTEGER NOT NULL,last_used_utc TEXT NOT NULL);
                    INSERT INTO line_cache(cache_key,profile_id,normalized_text_hash,model_hash,audio_path,duration,bytes,last_used_utc)
                      VALUES('official-line','official-interim','text','m','official.wav',1.0,1,'now');
                    CREATE TABLE pool_voice(
                      domain_id TEXT NOT NULL,language TEXT NOT NULL,sex TEXT NOT NULL,
                      profile_id TEXT NOT NULL UNIQUE REFERENCES voice_profile(id) ON DELETE CASCADE,
                      state INTEGER NOT NULL,assigned_speaker_id INTEGER REFERENCES speaker(id),
                      slot_traits_json TEXT,template_id TEXT NOT NULL,sequence INTEGER NOT NULL,
                      PRIMARY KEY(domain_id,language,sex,profile_id));
                    INSERT INTO pool_voice(
                      domain_id,language,sex,profile_id,state,assigned_speaker_id,template_id,sequence)
                      VALUES('ishgardian','english','feminine','official-interim',0,NULL,'official',0),
                            ('ishgardian','english','feminine','designed-interim',0,NULL,'designed',1);
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            using (var database = new Database(path))
            {
                var state = await database.ReadAsync(async connection =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT
                          (SELECT COUNT(*) FROM voice_profile WHERE id='official-interim'),
                          (SELECT COUNT(*) FROM speaker_voice WHERE profile_id='official-interim'),
                          (SELECT COUNT(*) FROM line_cache WHERE profile_id='official-interim'),
                          (SELECT COUNT(*) FROM official_reference_clip WHERE source_hash='interim-source'),
                          (SELECT MIN(language) FROM official_reference_clip WHERE source_hash='interim-source'),
                          (SELECT COUNT(*) FROM pool_voice WHERE profile_id='official-interim'),
                          (SELECT COUNT(*) FROM pool_voice WHERE profile_id='designed-interim')
                        """;
                    await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                    Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
                    return (
                        OfficialProfile: reader.GetInt32(0),
                        OfficialAssignment: reader.GetInt32(1),
                        OfficialCache: reader.GetInt32(2),
                        OfficialClip: reader.GetInt32(3),
                        OfficialClipLanguage: reader.GetString(4),
                        OfficialPool: reader.GetInt32(5),
                        DesignedPool: reader.GetInt32(6));
                }, TestContext.Current.CancellationToken);

                Assert.Equal(1, state.OfficialProfile);
                Assert.Equal(1, state.OfficialAssignment);
                Assert.Equal(1, state.OfficialCache);
                Assert.Equal(1, state.OfficialClip);
                Assert.Equal("und", state.OfficialClipLanguage);
                Assert.Equal(0, state.OfficialPool);
                Assert.Equal(1, state.DesignedPool);
            }

            using var reopened = new Database(path);
            var stableClip = await reopened.ReadAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT language FROM official_reference_clip WHERE source_hash='interim-source'";
                return (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);
            Assert.Equal("und", stableClip);
        }
        finally { Directory.Delete(root, true); }
    }
}
