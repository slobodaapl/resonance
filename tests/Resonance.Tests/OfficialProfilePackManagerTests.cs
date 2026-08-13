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

public sealed class OfficialProfilePackManagerTests
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
    public void CommittedPackExactlyCoversCatalogFallbackMatrix()
    {
        var path = ProjectPath("release-assets", "official-profiles", "official-profiles.sqlite3");
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
        using var models = JsonDocument.Parse(File.ReadAllText(ProjectPath("assets", "models.json")));
        var modelHashes = models.RootElement.GetProperty("artifacts").EnumerateArray()
            .Where(artifact => artifact.GetProperty("id").GetString() is "base-q4" or "base-q8")
            .Select(artifact => artifact.GetProperty("sha256").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
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
            Assert.Equal(4096, embeddingBytes.Length);
            Assert.Equal(rvqLength * codebooks * sizeof(int), codeBytes.Length);
            Assert.InRange(rvqLength, 1, 192);
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

    private static IEnumerable<(string Id, string? Sex, string? Age)> FallbackVariants(CastingDomain domain) =>
        domain.FallbackDimensions switch
        {
            "none" => [("default", null, null)],
            "feminine_only" => [("feminine", "feminine", null)],
            "sex" => [("masculine", "masculine", null), ("feminine", "feminine", null)],
            "sex_age" =>
            [
                ("masculine_young", "masculine", "young"),
                ("masculine_adult", "masculine", "adult"),
                ("feminine_young", "feminine", "young"),
                ("feminine_adult", "feminine", "adult"),
            ],
            _ => throw new InvalidDataException($"Unknown fallback dimensions '{domain.FallbackDimensions}'"),
        };

    [Fact]
    public async Task DownloadsOnceAndImportsOnlyExactModelWithoutReplacingExistingOfficial()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-profile-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var packDatabase = Path.Combine(root, "pack.sqlite3");
            CreatePackDatabase(packDatabase, "model-q4", "model-q8");
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
                profileCount = 2,
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
            CreatePackDatabase(localDatabase, "model-q4");
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
                    Length = 1, Sha256 = new string('a', 64), DatabaseSha256 = localHash, ProfileCount = 2,
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
                Length = 1, Sha256 = new string('b', 64), DatabaseSha256 = new string('c', 64), ProfileCount = 2,
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

    private static void CreatePackDatabase(string path, params string[] modelHashes)
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
                Enumerable.Repeat(0.1f, 1024).ToArray(), new int[16 * 187], 187, 16, "Reference");
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
