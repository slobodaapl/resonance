using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    public void CommittedPackContainsNoGameResourceCoordinates()
    {
        var path = ProjectPath("release-assets", "official-profiles", "official-profiles.sqlite3");
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM official_profile WHERE source_metadata <> '{}'";

        Assert.Equal(0L, (long)(command.ExecuteScalar() ?? -1L));
    }

    [Fact]
    public void CommittedOfficialProfilesCoverEveryBaseModelWithRuntimeCompatibleLatents()
    {
        var path = ProjectPath("release-assets", "official-profiles", "official-profiles.sqlite3");
        var modelDimensions = BaseModelDimensions();
        var coverage = modelDimensions.Keys.ToDictionary(
            modelHash => modelHash, _ => new HashSet<(string GroupId, string Language)>(), StringComparer.Ordinal);
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT group_id,language,model_hash,length(speaker_embedding),length(rvq_codes),rvq_length,codebooks
            FROM official_profile
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var modelHash = reader.GetString(2);
            Assert.True(coverage.TryGetValue(modelHash, out var modelCoverage), $"Unknown Base model hash {modelHash}");
            Assert.True(modelCoverage.Add((reader.GetString(0), reader.GetString(1))));
            Assert.Equal(modelDimensions[modelHash] * sizeof(float), reader.GetInt32(3));
            Assert.Equal(16, reader.GetInt32(6));
            Assert.InRange(reader.GetInt32(5), 1, 250);
            Assert.Equal(reader.GetInt32(5) * reader.GetInt32(6) * sizeof(int), reader.GetInt32(4));
        }
        var baseline = coverage.Values.First();
        Assert.NotEmpty(baseline);
        foreach (var (modelHash, modelCoverage) in coverage)
            Assert.True(modelCoverage.SetEquals(baseline), $"Official profile coverage differs for Base model {modelHash}");
    }

    [Fact]
    public async Task CommittedPackImportsAdditionalActorsForEveryBaseModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-real-pack-" + Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root, "data", "official-profile-pack");
        Directory.CreateDirectory(local);
        try
        {
            var path = Path.Combine(local, "official-profiles.sqlite3");
            File.Copy(ProjectPath("release-assets", "official-profiles", "official-profiles.sqlite3"), path);
            using (var source = new SqliteConnection($"Data Source={path};Mode=ReadOnly"))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "SELECT schema_version,pack_version,catalog_version FROM pack_metadata";
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                await File.WriteAllTextAsync(Path.Combine(local, "official-profile-pack.json"), JsonSerializer.Serialize(new
                {
                    SchemaVersion = reader.GetInt32(0), PackVersion = reader.GetInt32(1), CatalogVersion = reader.GetInt32(2),
                    Url = "https://example.invalid/pack.zip", Length = 1, Sha256 = new string('a', 64),
                    DatabaseSha256 = await HashAsync(path),
                }), TestContext.Current.CancellationToken);
            }
            using var database = new Database(Path.Combine(root, "user.sqlite3"));
            var voices = new VoiceRegistry(database);
            using var manager = new OfficialProfilePackManager(Path.Combine(root, "data"), database, voices,
                OfficialVoiceCatalog.Load(ProjectPath("assets", "official-voices.json")), CastingCatalog());
            foreach (var model in BaseModelDimensions().Keys)
            {
                Assert.True((await manager.SynchronizeAsync(model, TestContext.Current.CancellationToken,
                    allowRemoteUpdate: false)).ImportedProfiles > 0);
                foreach (var group in new[] { "cefca", "fandaniel", "oldtitania", "peddlerofnorvrandt", "rasho", "thordan" })
                {
                    var profile = await voices.GetBestVoiceByStableKeyAsync($"official:{group}", "english", model,
                        TestContext.Current.CancellationToken);
                    Assert.NotNull(profile);
                    Assert.Equal(VoiceProfileKind.Official, profile.Kind);
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CommittedPackExactlyCoversCatalogFallbackMatrix()
    {
        var path = ProjectPath("release-assets", "official-profiles", "official-profiles.sqlite3");
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
        var modelDimensions = BaseModelDimensions();
        var modelHashes = modelDimensions.Keys.ToHashSet(StringComparer.Ordinal);
        var languages = new HashSet<string>(["english", "japanese", "german", "french"], StringComparer.Ordinal);
        var expected = catalog.Domains.SelectMany(domain => FallbackVariants(domain)
                .SelectMany(variant => languages.SelectMany(language => modelHashes.Select(modelHash =>
                    (domain.Id, variant.Id, variant.Sex, variant.Age, language, modelHash)))))
            .ToHashSet();

        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var metadataCommand = connection.CreateCommand();
        metadataCommand.CommandText = "SELECT catalog_version FROM pack_metadata LIMIT 1";
        var catalogVersion = Convert.ToInt32(metadataCommand.ExecuteScalar());
        Assert.Equal(catalog.Version, catalogVersion);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT domain_id,variant_id,sex,age,language,model_hash,
                   speaker_embedding,rvq_codes,rvq_length,codebooks,ref_text,profile_hash
            FROM fallback_profile ORDER BY domain_id,variant_id,language,model_hash
            """;
        using var reader = command.ExecuteReader();
        var actual = new HashSet<(string, string, string?, string?, string, string)>();
        while (reader.Read())
        {
            var row = (reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.GetString(5));
            Assert.True(actual.Add(row), $"Duplicate fallback row: {row}");
            var embeddingBytes = (byte[])reader[6];
            var codeBytes = (byte[])reader[7];
            var rvqLength = reader.GetInt32(8);
            var codebooks = reader.GetInt32(9);
            Assert.Equal(modelDimensions[row.Item6] * sizeof(float), embeddingBytes.Length);
            Assert.Equal(rvqLength * codebooks * sizeof(int), codeBytes.Length);
            Assert.InRange(rvqLength, 1, 250);
            Assert.Equal(16, codebooks);
            var embedding = new float[embeddingBytes.Length / sizeof(float)];
            var codes = new int[codeBytes.Length / sizeof(int)];
            Buffer.BlockCopy(embeddingBytes, 0, embedding, 0, embeddingBytes.Length);
            Buffer.BlockCopy(codeBytes, 0, codes, 0, codeBytes.Length);
            var profile = VoiceRegistry.CreateProfile(
                VoiceProfileKind.Designed, row.Item5, row.Item6, null, null, null,
                new VoiceReference(embedding, codes, rvqLength, codebooks, reader.GetString(10)),
                "{}", row.Item1, catalogVersion, "{}");
            Assert.Equal(reader.GetString(11), profile.ProfileHash, ignoreCase: true);
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RegionalReferencesKeepTheirDeclaredNativePerformanceIntact()
    {
        using var recipe = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ProjectPath("assets","fallback-voices.json")));
        var actors = recipe.RootElement.GetProperty("foundations").EnumerateArray()
            .ToDictionary(f => f.GetProperty("id").GetString()!,f => f.GetProperty("nativeActor").GetString()!);
        var recordings = recipe.RootElement.GetProperty("domains").EnumerateArray()
            .Where(domain => domain.TryGetProperty("recording",out _))
            .Select(domain => domain.GetProperty("id").GetString()!).ToHashSet();
        var nativeBlends = recipe.RootElement.GetProperty("domains").EnumerateArray()
            .Where(domain => domain.TryGetProperty("nativeBlend", out _))
            .ToDictionary(domain => domain.GetProperty("id").GetString()!, domain => domain.GetProperty("nativeBlend")
                .EnumerateObject().MaxBy(anchor => anchor.Value.GetDouble()).Name);
        using var connection = new SqliteConnection($"Data Source={ProjectPath("release-assets","official-profiles","official-profiles.sqlite3")};Mode=ReadOnly");
        connection.Open();
        var native = new Dictionary<(string,string,string),(string,byte[],int,int)>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT group_id,language,model_hash,ref_text,rvq_codes,rvq_length,codebooks FROM official_profile";
            using var reader = query.ExecuteReader();
            while (reader.Read())
                native[(reader.GetString(0),reader.GetString(1),reader.GetString(2))] =
                    (reader.GetString(3),(byte[])reader[4],reader.GetInt32(5),reader.GetInt32(6));
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT variant_id,language,model_hash,ref_text,rvq_codes,rvq_length,codebooks,domain_id FROM fallback_profile";
        using var profiles = command.ExecuteReader();
        var count = 0;
        while (profiles.Read())
        {
            if (recordings.Contains(profiles.GetString(7))) continue;
            var actor = nativeBlends.GetValueOrDefault(profiles.GetString(7)) ?? actors[profiles.GetString(0)];
            var expected = native[(actor,profiles.GetString(1),profiles.GetString(2))];
            Assert.Equal(expected.Item1,profiles.GetString(3));
            Assert.Equal(expected.Item2,(byte[])profiles[4]);
            Assert.Equal(expected.Item3,profiles.GetInt32(5));
            Assert.Equal(expected.Item4,profiles.GetInt32(6));
            count++;
        }
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets","dub-profiles.json"));
        Assert.Equal(catalog.Domains.Where(domain => !recordings.Contains(domain.Id))
            .Sum(domain => FallbackVariants(domain).Count()) * 4 * 4,count);
    }

    [Fact]
    public void RecordedSharedFallbackKeepsOneCompleteReferenceAcrossLanguages()
    {
        using var recipe = JsonDocument.Parse(File.ReadAllText(ProjectPath("assets","fallback-voices.json")));
        var recordings = recipe.RootElement.GetProperty("domains").EnumerateArray()
            .Where(domain => domain.TryGetProperty("recording",out _)).ToArray();
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets","dub-profiles.json"));
        using var connection = new SqliteConnection($"Data Source={ProjectPath("release-assets","official-profiles","official-profiles.sqlite3")};Mode=ReadOnly");
        connection.Open();
        foreach (var recording in recordings)
        {
            var domain = recording.GetProperty("id").GetString()!;
            Assert.True(catalog.GetDomain(domain).SharedVoice);
            foreach (var (model,dimension) in BaseModelDimensions())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT variant_id,ref_text,speaker_embedding,rvq_codes,rvq_length,codebooks FROM fallback_profile WHERE domain_id=$domain AND model_hash=$model ORDER BY language";
                command.Parameters.AddWithValue("$domain",domain); command.Parameters.AddWithValue("$model",model);
                using var reader = command.ExecuteReader();
                byte[]? embedding = null, codes = null; var count = 0;
                while (reader.Read())
                {
                    Assert.Equal("default",reader.GetString(0));
                    Assert.Equal(recording.GetProperty("recording").GetProperty("transcript").GetString(),reader.GetString(1));
                    var currentEmbedding = (byte[])reader[2]; var currentCodes = (byte[])reader[3];
                    Assert.Equal(dimension*sizeof(float),currentEmbedding.Length);
                    Assert.InRange(reader.GetInt32(4),1,250);
                    Assert.Equal(reader.GetInt32(4)*reader.GetInt32(5)*sizeof(int),currentCodes.Length);
                    if (embedding is not null) Assert.Equal(embedding,currentEmbedding);
                    if (codes is not null) Assert.Equal(codes,currentCodes);
                    embedding = currentEmbedding; codes = currentCodes; count++;
                }
                Assert.Equal(4,count);
            }
        }
    }

    private static IEnumerable<(string Id, string? Sex, string? Age)> FallbackVariants(CastingDomain domain) =>
        domain.FallbackDimensions switch
        {
            "none" => [("default", null, null)],
            "feminine_only" => [("feminine_young", "feminine", "young"),
                ("feminine_adult", "feminine", "adult"), ("feminine_elder", "feminine", "elder")],
            "sex" => [("masculine", "masculine", null), ("feminine", "feminine", null)],
            "sex_age" =>
            [
                ("masculine_young", "masculine", "young"),
                ("masculine_adult", "masculine", "adult"),
                ("masculine_elder", "masculine", "elder"),
                ("feminine_young", "feminine", "young"),
                ("feminine_adult", "feminine", "adult"),
                ("feminine_elder", "feminine", "elder"),
            ],
            _ => throw new InvalidDataException($"Unknown fallback dimensions '{domain.FallbackDimensions}'"),
        };

    private static Dictionary<string, int> BaseModelDimensions()
    {
        using var models = JsonDocument.Parse(File.ReadAllText(ProjectPath("assets", "models.json")));
        return models.RootElement.GetProperty("artifacts").EnumerateArray()
            .Where(artifact => artifact.GetProperty("id").GetString()!.StartsWith("base-", StringComparison.Ordinal))
            .ToDictionary(artifact => artifact.GetProperty("sha256").GetString()!,
                artifact => artifact.GetProperty("id").GetString()!.Contains("1.7b", StringComparison.Ordinal)
                    ? 2048
                    : 1024,
                StringComparer.Ordinal);
    }

    [Fact]
    public async Task DownloadsOnceAndImportsOnlyExactModelWithoutReplacingExistingOfficial()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-profile-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var packDatabase = Path.Combine(root, "pack.sqlite3");
            CreatePackDatabase(packDatabase, 2048, "model-q4", "model-q8");
            var archive = Path.Combine(root, "pack.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(packDatabase, "official-profiles.sqlite3", CompressionLevel.SmallestSize);
            var archiveBytes = await File.ReadAllBytesAsync(archive, TestContext.Current.CancellationToken);
            var databaseHash = await HashAsync(packDatabase);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                packVersion = 7,
                url = "https://example.invalid/pack.zip",
                length = archiveBytes.Length,
                sha256 = Hash(archiveBytes),
                databaseSha256 = databaseHash,
            });
            var handler = new PackHandler(manifest, archiveBytes);
            using var http = new HttpClient(handler);
            using var userDatabase = new Database(Path.Combine(root, "user.sqlite3"));
            var voices = new VoiceRegistry(userDatabase);
            var catalog = OfficialVoiceCatalog.Parse("""
                {
                  "schemaVersion": 1, "catalogVersion": 1,
                  "groups": [{
                    "id": "actor", "label": "Actor", "npcBaseIds": [],
                    "aliases": {}, "sources": {}, "actorTokens": ["ACTOR"]
                  }]
                }
                """);
            var speaker = await voices.ResolveSpeakerAsync("official:actor", null, "Actor", 0, "english",
                TestContext.Current.CancellationToken);
            await voices.SaveAndAssignAsync(speaker.Id,
                VoiceRegistry.CreateProfile(VoiceProfileKind.Designed, "english", "model-q4", 1,
                    "designed", 1, new VoiceReference([0.1f], [1], 1, 1, "designed")),
                TestContext.Current.CancellationToken);
            using var manager = new OfficialProfilePackManager(
                Path.Combine(root, "data"), userDatabase, voices, catalog, CastingCatalog(), http,
                "https://example.invalid/manifest.json");

            var first = await manager.SynchronizeAsync("model-q4", TestContext.Current.CancellationToken);
            var second = await manager.SynchronizeAsync("model-q4", TestContext.Current.CancellationToken);

            Assert.True(first.Downloaded);
            Assert.Equal(2, first.ImportedProfiles);
            Assert.False(second.Downloaded);
            Assert.Equal(0, second.ImportedProfiles);
            Assert.Equal(1, handler.PackRequests);
            Assert.Equal(VoiceProfileKind.Official, (await voices.GetBestVoiceAsync(
                speaker.Id, "english", "model-q4", TestContext.Current.CancellationToken))?.Kind);
            Assert.Null(await voices.GetBestVoiceAsync(
                speaker.Id, "english", "model-q8", TestContext.Current.CancellationToken));
            Assert.NotNull(await voices.GetBestVoiceByStableKeyAsync(
                VoiceRegistry.DomainFallbackSpeakerKey("il_mheg_nu_mou"), "english", "model-q4",
                TestContext.Current.CancellationToken));

            handler.FailManifest = true;
            using var offlineDatabase = new Database(Path.Combine(root, "offline.sqlite3"));
            var offlineVoices = new VoiceRegistry(offlineDatabase);
            using var offlineManager = new OfficialProfilePackManager(
                Path.Combine(root, "data"), offlineDatabase, offlineVoices, catalog, CastingCatalog(), http,
                "https://example.invalid/manifest.json");

            var offline = await offlineManager.SynchronizeAsync(
                "model-q4", TestContext.Current.CancellationToken);

            Assert.False(offline.Downloaded);
            Assert.Equal(2, offline.ImportedProfiles);
            Assert.Equal(VoiceProfileKind.Official, (await offlineVoices.GetBestVoiceByStableKeyAsync(
                "official:actor", "english", "model-q4", TestContext.Current.CancellationToken))?.Kind);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NewerLocallyInstalledPackIsNotDowngradedByOlderRemoteManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-profile-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data", "official-profile-pack"));
        try
        {
            var localDatabase = Path.Combine(root, "data", "official-profile-pack", "official-profiles.sqlite3");
            CreatePackDatabase(localDatabase, 1024, "model-q4");
            using (var connection = new SqliteConnection($"Data Source={localDatabase}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE pack_metadata SET pack_version=8";
                command.ExecuteNonQuery();
            }
            var localHash = await HashAsync(localDatabase);
            await File.WriteAllTextAsync(
                Path.Combine(root, "data", "official-profile-pack", "official-profile-pack.json"),
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1, PackVersion = 8, Url = "https://example.invalid/local.zip",
                    Length = 1, Sha256 = new string('a', 64), DatabaseSha256 = localHash,
                }), TestContext.Current.CancellationToken);
            using var database = new Database(Path.Combine(root, "user.sqlite3"));
            var voices = new VoiceRegistry(database);
            var catalog = OfficialVoiceCatalog.Parse("""
                {"schemaVersion":1,"catalogVersion":1,"groups":[{"id":"actor","label":"Actor",
                "npcBaseIds":[],"aliases":{},"sources":{},"actorTokens":["ACTOR"]}]}
                """);
            var remote = JsonSerializer.SerializeToUtf8Bytes(new
            {
                SchemaVersion = 1, PackVersion = 7, Url = "https://example.invalid/remote.zip",
                Length = 1, Sha256 = new string('b', 64), DatabaseSha256 = new string('c', 64),
            });
            var handler = new PackHandler(remote, [0]);
            using var http = new HttpClient(handler);
            using var manager = new OfficialProfilePackManager(
                Path.Combine(root, "data"), database, voices, catalog, CastingCatalog(), http,
                "https://example.invalid/manifest.json");

            var localOnly = await manager.SynchronizeAsync(
                "model-q4", TestContext.Current.CancellationToken, allowRemoteUpdate: false);

            Assert.Equal(8, localOnly.PackVersion);
            Assert.False(localOnly.Downloaded);
            Assert.Equal(0, handler.ManifestRequests);

            var result = await manager.SynchronizeAsync("model-q4", TestContext.Current.CancellationToken);

            Assert.Equal(8, result.PackVersion);
            Assert.False(result.Downloaded);
            Assert.Equal(1, handler.ManifestRequests);
            Assert.Equal(0, handler.PackRequests);
            Assert.Equal(localHash, await HashAsync(localDatabase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CyclicSceneImportRequiresAStartButDoesNotRequireAnInventedEnd(bool hasStart)
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-cyclic-pack-" + Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root, "data", "official-profile-pack");
        Directory.CreateDirectory(local);
        try
        {
            var path = Path.Combine(local, "official-profiles.sqlite3");
            CreatePackDatabase(path, 1024, "model-q4");
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE pack_metadata SET schema_version=2;
                    CREATE TABLE scene_node(cutscene_id INTEGER,language TEXT,node_id TEXT,
                      sheet_key_hash TEXT,occurrence INTEGER,actor_npc_base_id INTEGER,
                      official_group_id TEXT,actor_token_hash TEXT,node_kind TEXT);
                    CREATE TABLE scene_edge(cutscene_id INTEGER,language TEXT,current_node_id TEXT,next_node_id TEXT);
                    INSERT INTO scene_node VALUES(1,'english','loop','key',0,NULL,'actor',NULL,'synthetic');
                    INSERT INTO scene_edge VALUES(1,'english','loop','loop');
                    """;
                command.ExecuteNonQuery();
                if (hasStart)
                {
                    command.CommandText = "INSERT INTO scene_edge VALUES(1,'english',NULL,'loop')";
                    command.ExecuteNonQuery();
                }
            }
            await File.WriteAllTextAsync(Path.Combine(local, "official-profile-pack.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = 2, PackVersion = 7, Url = "https://example.invalid/pack.zip", Length = 1,
                Sha256 = new string('a', 64), DatabaseSha256 = await HashAsync(path),
            }), TestContext.Current.CancellationToken);
            using var database = new Database(Path.Combine(root, "user.sqlite3"));
            var voices = new VoiceRegistry(database);
            var catalog = OfficialVoiceCatalog.Parse("""
                {"schemaVersion":1,"catalogVersion":1,"groups":[{"id":"actor","label":"Actor",
                "npcBaseIds":[],"aliases":{},"sources":{},"actorTokens":["ACTOR"]}]}
                """);
            using var manager = new OfficialProfilePackManager(Path.Combine(root, "data"), database, voices,
                catalog, CastingCatalog());
            var import = manager.SynchronizeAsync("model-q4", TestContext.Current.CancellationToken, allowRemoteUpdate: false);
            if (hasStart) Assert.True((await import).ImportedProfiles > 0);
            else await Assert.ThrowsAsync<InvalidDataException>(() => import);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CreatePackDatabase(string path, int embeddingLength, params string[] modelHashes)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE pack_metadata(schema_version INTEGER NOT NULL,pack_version INTEGER NOT NULL,
              catalog_version INTEGER NOT NULL,created_utc TEXT NOT NULL);
            INSERT INTO pack_metadata VALUES(1,7,1,'now');
            CREATE TABLE official_profile(
              group_id TEXT NOT NULL,label TEXT NOT NULL,actor_token TEXT NOT NULL,
              language TEXT NOT NULL,model_hash TEXT NOT NULL,ref_text TEXT NOT NULL,
              speaker_embedding BLOB NOT NULL,rvq_codes BLOB NOT NULL,rvq_length INTEGER NOT NULL,
              codebooks INTEGER NOT NULL,source_metadata TEXT NOT NULL,profile_hash TEXT NOT NULL,
              PRIMARY KEY(group_id,language,model_hash));
            CREATE TABLE fallback_profile(
              domain_id TEXT NOT NULL,variant_id TEXT NOT NULL,sex TEXT NULL,age TEXT NULL,
              language TEXT NOT NULL,model_hash TEXT NOT NULL,
              ref_text TEXT NOT NULL,speaker_embedding BLOB NOT NULL,rvq_codes BLOB NOT NULL,
              rvq_length INTEGER NOT NULL,codebooks INTEGER NOT NULL,profile_hash TEXT NOT NULL,
              PRIMARY KEY(domain_id,variant_id,language,model_hash));
            """;
        command.ExecuteNonQuery();
        foreach (var modelHash in modelHashes)
        {
            var reference = new VoiceReference(
                Enumerable.Repeat(0.1f, embeddingLength).ToArray(), new int[16 * 187], 187, 16, "Reference");
            var profile = VoiceRegistry.CreateProfile(VoiceProfileKind.Official, "english", modelHash,
                null, null, null, reference, "source");
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO official_profile VALUES(
                  'actor','Actor','ACTOR','english',$model,'Reference',$embedding,$codes,187,16,
                  'source',$hash)
                """;
            command.Parameters.AddWithValue("$model", modelHash);
            command.Parameters.AddWithValue("$embedding", Bytes(reference.SpeakerEmbedding));
            command.Parameters.AddWithValue("$codes", Bytes(reference.RvqCodes));
            command.Parameters.AddWithValue("$hash", profile.ProfileHash);
            command.ExecuteNonQuery();
            var fallback = VoiceRegistry.CreateProfile(
                VoiceProfileKind.Designed, "english", modelHash, null, null, null, reference,
                "{}", "il_mheg_nu_mou", 1, "{}");
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO fallback_profile VALUES(
                  'il_mheg_nu_mou','default',NULL,NULL,'english',$model,
                  'Reference',$embedding,$codes,187,16,$hash)
                """;
            command.Parameters.AddWithValue("$model", modelHash);
            command.Parameters.AddWithValue("$embedding", Bytes(reference.SpeakerEmbedding));
            command.Parameters.AddWithValue("$codes", Bytes(reference.RvqCodes));
            command.Parameters.AddWithValue("$hash", fallback.ProfileHash);
            command.ExecuteNonQuery();
        }
    }

    private static byte[] Bytes<T>(T[] values) where T : struct =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static CastingProfileCatalog CastingCatalog() =>
        CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string ProjectPath(params string[] parts)
    {
        var path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "Resonance.csproj")))
            path = Directory.GetParent(path)?.FullName
                   ?? throw new DirectoryNotFoundException("Project root not found");
        return Path.Combine([path, .. parts]);
    }

    private sealed class PackHandler(byte[] manifest, byte[] archive) : HttpMessageHandler
    {
        public int ManifestRequests { get; private set; }
        public int PackRequests { get; private set; }
        public bool FailManifest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal))
            {
                ManifestRequests++;
                if (FailManifest) throw new HttpRequestException("offline");
                return Task.FromResult(Response(manifest, "application/json"));
            }
            PackRequests++;
            return Task.FromResult(Response(archive, "application/zip"));
        }

        private static HttpResponseMessage Response(byte[] body, string contentType) => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) },
            },
        };
    }
}
