using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Resonance.Data;
using Resonance.Game;
using Resonance.Tts;

namespace Resonance.Bootstrap;

public sealed record OfficialProfilePackSyncResult(
    int ImportedProfiles,
    int PackVersion,
    bool Downloaded);

public sealed class OfficialProfilePackManager : IDisposable
{
    public const string DefaultManifestUrl =
        "https://slobodaapl.github.io/resonance/official-profile-pack.json";
    private const int CurrentSchemaVersion = 2;
    private const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    private const long MaximumDatabaseBytes = 2L * 1024 * 1024 * 1024;
    private readonly string directory;
    private readonly Database database;
    private readonly VoiceRegistry voices;
    private readonly OfficialVoiceCatalog catalog;
    private readonly CastingProfileCatalog castingCatalog;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly Uri manifestUri;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int disposed;

    public OfficialProfilePackManager(
        string dataDirectory,
        Database database,
        VoiceRegistry voices,
        OfficialVoiceCatalog catalog,
        CastingProfileCatalog castingCatalog,
        HttpClient? httpClient = null,
        string manifestUrl = DefaultManifestUrl)
    {
        directory = Path.Combine(Path.GetFullPath(dataDirectory), "official-profile-pack");
        this.database = database;
        this.voices = voices;
        this.catalog = catalog;
        this.castingCatalog = castingCatalog;
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        manifestUri = new Uri(manifestUrl, UriKind.Absolute);
        if (manifestUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Official profile manifest must use HTTPS", nameof(manifestUrl));
        Directory.CreateDirectory(directory);
    }

    public async Task<OfficialProfilePackSyncResult> SynchronizeAsync(
        string modelHash,
        CancellationToken token,
        bool allowRemoteUpdate = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!allowRemoteUpdate)
            {
                var local = await LoadLocalManifestAsync(token).ConfigureAwait(false);
                if (local is null || !File.Exists(DatabasePath)) return new(0, 0, false);
                return new(await ImportAsync(DatabasePath, modelHash, local.PackVersion, token)
                        .ConfigureAwait(false),
                    local.PackVersion, false);
            }

            RemoteManifest? remote;
            try
            {
                remote = await FetchManifestAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                var cached = await LoadLocalManifestAsync(token).ConfigureAwait(false);
                if (cached is null || !File.Exists(DatabasePath)) throw;
                return new(await ImportAsync(
                        DatabasePath, modelHash, cached.PackVersion, token).ConfigureAwait(false),
                    cached.PackVersion, false);
            }
            if (remote is null)
            {
                var local = await LoadLocalManifestAsync(token).ConfigureAwait(false);
                if (local is null || !File.Exists(DatabasePath)) return new(0, 0, false);
                return new(await ImportAsync(DatabasePath, modelHash, local.PackVersion, token).ConfigureAwait(false),
                    local.PackVersion, false);
            }

            var localManifest = await LoadLocalManifestAsync(token).ConfigureAwait(false);
            if (localManifest is not null && File.Exists(DatabasePath)
                && localManifest.PackVersion > remote.PackVersion)
                return new(await ImportAsync(DatabasePath, modelHash, localManifest.PackVersion, token)
                        .ConfigureAwait(false),
                    localManifest.PackVersion, false);
            var downloaded = localManifest is null
                             || localManifest.PackVersion != remote.PackVersion
                             || !String.Equals(localManifest.DatabaseSha256, remote.DatabaseSha256,
                                 StringComparison.OrdinalIgnoreCase)
                             || !File.Exists(DatabasePath);
            if (downloaded) await DownloadAndInstallAsync(remote, token).ConfigureAwait(false);
            var imported = await ImportAsync(DatabasePath, modelHash, remote.PackVersion, token)
                .ConfigureAwait(false);
            return new(imported, remote.PackVersion, downloaded);
        }
        finally { gate.Release(); }
    }

    private string DatabasePath => Path.Combine(directory, "official-profiles.sqlite3");
    private string LocalManifestPath => Path.Combine(directory, "official-profile-pack.json");

    private async Task<RemoteManifest?> FetchManifestAsync(CancellationToken token)
    {
        using var response = await http.GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var manifest = await JsonSerializer.DeserializeAsync<RemoteManifest>(stream,
            JsonOptions(), token).ConfigureAwait(false)
                       ?? throw new InvalidDataException("Official profile pack manifest is empty");
        ValidateManifest(manifest);
        return manifest;
    }

    private async Task<RemoteManifest?> LoadLocalManifestAsync(CancellationToken token)
    {
        if (!File.Exists(LocalManifestPath)) return null;
        await using var stream = File.OpenRead(LocalManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<RemoteManifest>(stream,
            JsonOptions(), token).ConfigureAwait(false);
        if (manifest is null) return null;
        ValidateManifest(manifest);
        return manifest;
    }

    private async Task DownloadAndInstallAsync(RemoteManifest manifest, CancellationToken token)
    {
        var archivePartial = Path.Combine(directory, "official-profiles.zip.partial");
        var databasePartial = DatabasePath + ".partial";
        try
        {
            using (var response = await http.GetAsync(
                       manifest.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var output = new FileStream(archivePartial, FileMode.Create, FileAccess.Write,
                    FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await CopyBoundedAsync(input, output, manifest.Length, token).ConfigureAwait(false);
            }
            if (!String.Equals(await Sha256Async(archivePartial, token).ConfigureAwait(false), manifest.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Official profile pack archive failed SHA-256 verification");

            using (var archive = ZipFile.OpenRead(archivePartial))
            {
                var entries = archive.Entries.Where(entry => !String.IsNullOrEmpty(entry.Name)).ToArray();
                var databaseEntry = entries.SingleOrDefault(entry =>
                    String.Equals(entry.FullName, "official-profiles.sqlite3", StringComparison.Ordinal));
                if (databaseEntry is null || databaseEntry.Length <= 0 || databaseEntry.Length > MaximumDatabaseBytes
                    || manifest.DatabaseLength is > 0 && databaseEntry.Length != manifest.DatabaseLength)
                    throw new InvalidDataException("Official profile pack has no valid database entry");
                await using var input = databaseEntry.Open();
                await using var output = new FileStream(databasePartial, FileMode.Create, FileAccess.Write,
                    FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await CopyBoundedAsync(input, output, databaseEntry.Length, token).ConfigureAwait(false);
            }
            if (!String.Equals(await Sha256Async(databasePartial, token).ConfigureAwait(false),
                    manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Official profile pack database failed SHA-256 verification");
            ValidateDatabase(databasePartial, manifest.PackVersion, manifest.CatalogVersion,
                manifest.SchemaVersion);
            File.Move(databasePartial, DatabasePath, true);
            await WriteManifestAtomicAsync(manifest, token).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(archivePartial);
            TryDelete(databasePartial);
        }
    }

    private async Task<int> ImportAsync(
        string packPath,
        string modelHash,
        int packVersion,
        CancellationToken token)
    {
        ValidateDatabase(packPath, packVersion);
        int packCatalogVersion;
        await using (var source = OpenReadOnly(packPath))
        await using (var command = source.CreateCommand())
        {
            command.CommandText = "SELECT catalog_version FROM pack_metadata LIMIT 1";
            packCatalogVersion = Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
        }
        var rows = new List<PackProfile>();
        await using (var source = OpenReadOnly(packPath))
        {
            await using var command = source.CreateCommand();
            command.CommandText = """
                SELECT group_id,label,actor_token,language,model_hash,ref_text,
                       speaker_embedding,rvq_codes,rvq_length,codebooks,
                       source_metadata,profile_hash
                FROM official_profile WHERE model_hash=$model ORDER BY group_id,language
                """;
            command.Parameters.AddWithValue("$model", modelHash);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                rows.Add(ReadProfile(reader));
        }

        var imported = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            var group = catalog.GetGroup(row.GroupId)
                        ?? throw new InvalidDataException($"Pack references unknown official group '{row.GroupId}'");
            if (!group.ExactActorTokens.Any(value => String.Equals(
                    NormalizeToken(value), NormalizeToken(row.ActorToken), StringComparison.Ordinal)))
                throw new InvalidDataException($"Pack actor token does not belong to group '{row.GroupId}'");
            var canonicalKey = OfficialVoiceCatalog.CanonicalSpeakerKey(group.Id);
            var existing = await voices.GetBestVoiceByStableKeyAsync(
                canonicalKey, row.Language, modelHash, token).ConfigureAwait(false);
            var reference = new VoiceReference(row.Embedding, row.Codes, row.RvqLength,
                row.Codebooks, row.Transcript);
            var profile = VoiceRegistry.CreateProfile(
                VoiceProfileKind.Official, row.Language, row.ModelHash, null, null, null,
                reference, row.SourceMetadata);
            if (!String.Equals(profile.ProfileHash, row.ProfileHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Pack profile hash failed for '{row.GroupId}/{row.Language}'");
            if (existing is { Kind: VoiceProfileKind.Official }
                && String.Equals(existing.ProfileHash, profile.ProfileHash, StringComparison.OrdinalIgnoreCase))
                continue;
            var speaker = await voices.ResolveSpeakerAsync(
                canonicalKey, null, group.Label, 0, row.Language, token).ConfigureAwait(false);
            await voices.SaveAndAssignAsync(speaker.Id, profile, token).ConfigureAwait(false);
            imported++;
        }
        await using (var source = OpenReadOnly(packPath))
        {
            if (!TableExists(source, "fallback_profile")) return imported;
            await using var command = source.CreateCommand();
            command.CommandText = """
                SELECT domain_id,variant_id,sex,age,language,model_hash,ref_text,speaker_embedding,rvq_codes,
                       rvq_length,codebooks,profile_hash
                FROM fallback_profile WHERE model_hash=$model ORDER BY domain_id,language
                """;
            command.Parameters.AddWithValue("$model", modelHash);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var row = ReadFallbackProfile(reader);
                ValidateFallbackProfile(row);
                var reference = new VoiceReference(row.Embedding, row.Codes, row.RvqLength,
                    row.Codebooks, row.Transcript);
                var profile = VoiceRegistry.CreateProfile(
                    VoiceProfileKind.Designed, row.Language, row.ModelHash, null, null, null,
                    reference, "{}", row.DomainId, packCatalogVersion, "{}");
                if (!String.Equals(profile.ProfileHash, row.ProfileHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Fallback profile hash failed for '{row.DomainId}/{row.Language}'");
                var speaker = await voices.ResolveSpeakerAsync(
                    VoiceRegistry.DomainFallbackSpeakerKey(row.DomainId, row.VariantId), null, row.DomainId, 0,
                    row.Language, token).ConfigureAwait(false);
                var existing = await voices.GetBestVoiceAsync(
                    speaker.Id, row.Language, row.ModelHash, token).ConfigureAwait(false);
                if (existing is null
                    || !String.Equals(existing.ProfileHash, profile.ProfileHash, StringComparison.OrdinalIgnoreCase))
                {
                    await voices.SaveAndAssignAsync(speaker.Id, profile, token).ConfigureAwait(false);
                    imported++;
                }
            }
        }
        return imported;
    }

    private void ValidateFallbackProfile(FallbackProfile row)
    {
        CastingDomain domain;
        try { domain = castingCatalog.GetDomain(row.DomainId); }
        catch (KeyNotFoundException error)
        {
            throw new InvalidDataException($"Pack references unknown fallback domain '{row.DomainId}'", error);
        }

        var valid = domain.FallbackDimensions switch
        {
            "none" => row is { VariantId: "default", Sex: null, Age: null },
            "feminine_only" => row is { VariantId: "feminine", Sex: "feminine", Age: null },
            "sex" => row.Age is null
                     && row.Sex is "masculine" or "feminine"
                     && String.Equals(row.VariantId, row.Sex, StringComparison.Ordinal),
            "sex_age" => row.Sex is "masculine" or "feminine"
                         && row.Age is "young" or "adult"
                         && String.Equals(row.VariantId, $"{row.Sex}_{row.Age}", StringComparison.Ordinal),
            _ => false,
        };
        if (!valid)
            throw new InvalidDataException(
                $"Fallback profile '{row.DomainId}/{row.VariantId}' does not match '{domain.FallbackDimensions}' dimensions");
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table)";
        command.Parameters.AddWithValue("$table", table);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static FallbackProfile ReadFallbackProfile(SqliteDataReader reader)
    {
        var embeddingBytes = (byte[])reader[7];
        var codeBytes = (byte[])reader[8];
        if (embeddingBytes.Length is not (1024 * sizeof(float) or 2048 * sizeof(float)) || codeBytes.Length == 0
            || codeBytes.Length % sizeof(int) != 0)
            throw new InvalidDataException("Fallback profile pack contains an invalid latent shape");
        var embedding = new float[embeddingBytes.Length / sizeof(float)];
        var codes = new int[codeBytes.Length / sizeof(int)];
        Buffer.BlockCopy(embeddingBytes, 0, embedding, 0, embeddingBytes.Length);
        Buffer.BlockCopy(codeBytes, 0, codes, 0, codeBytes.Length);
        var rvqLength = reader.GetInt32(9);
        var codebooks = reader.GetInt32(10);
        if (codebooks != 16 || rvqLength is < 1 or > 192 || codes.Length != rvqLength * codebooks
            || embedding.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Fallback profile pack contains invalid reference metadata");
        return new(reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6),
            embedding, codes, rvqLength, codebooks, reader.GetString(11));
    }

    private static PackProfile ReadProfile(SqliteDataReader reader)
    {
        var embeddingBytes = (byte[])reader[6];
        var codeBytes = (byte[])reader[7];
        if (embeddingBytes.Length is not (1024 * sizeof(float) or 2048 * sizeof(float)) || codeBytes.Length == 0
            || codeBytes.Length % sizeof(int) != 0)
            throw new InvalidDataException("Official profile pack contains an invalid latent shape");
        var embedding = new float[embeddingBytes.Length / sizeof(float)];
        var codes = new int[codeBytes.Length / sizeof(int)];
        Buffer.BlockCopy(embeddingBytes, 0, embedding, 0, embeddingBytes.Length);
        Buffer.BlockCopy(codeBytes, 0, codes, 0, codeBytes.Length);
        var rvqLength = reader.GetInt32(8);
        var codebooks = reader.GetInt32(9);
        if (codebooks != 16 || rvqLength is < 1 or > 250 || codes.Length != rvqLength * codebooks
            || embedding.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Official profile pack contains invalid reference metadata");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), embedding, codes, rvqLength, codebooks,
            reader.GetString(10), reader.GetString(11));
    }

    private static void ValidateDatabase(
        string path,
        int expectedPackVersion,
        int? expectedCatalogVersion = null,
        int? expectedSchemaVersion = null)
    {
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        if (!String.Equals((string?)command.ExecuteScalar(), "ok", StringComparison.Ordinal))
            throw new InvalidDataException("Official profile pack database integrity check failed");
        command.CommandText = "SELECT schema_version,pack_version,catalog_version FROM pack_metadata LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) is not (1 or CurrentSchemaVersion)
            || reader.GetInt32(1) != expectedPackVersion
            || expectedSchemaVersion is { } expectedSchema && reader.GetInt32(0) != expectedSchema
            || expectedCatalogVersion is { } catalogVersion && reader.GetInt32(2) != catalogVersion)
            throw new InvalidDataException("Official profile pack database metadata is incompatible");
        var schemaVersion = reader.GetInt32(0);
        reader.Close();
        if (schemaVersion == CurrentSchemaVersion)
        {
            if (!TableExists(connection, "scene_node") || !TableExists(connection, "scene_edge"))
                throw new InvalidDataException("Official profile pack scene graph tables are missing");
            command.CommandText = """
                SELECT COUNT(*) FROM scene_edge e
                LEFT JOIN scene_node c ON c.cutscene_id=e.cutscene_id AND c.language=e.language
                  AND c.node_id=e.current_node_id
                LEFT JOIN scene_node n ON n.cutscene_id=e.cutscene_id AND n.language=e.language
                  AND n.node_id=e.next_node_id
                WHERE (e.current_node_id IS NOT NULL AND c.node_id IS NULL)
                   OR (e.next_node_id IS NOT NULL AND n.node_id IS NULL)
                """;
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
                throw new InvalidDataException("Official profile pack scene graph contains dangling edges");
            command.CommandText = "SELECT COUNT(*) FROM scene_node";
            if (Convert.ToInt64(command.ExecuteScalar()) == 0)
                throw new InvalidDataException("Official profile pack scene graph is empty");
            command.CommandText = """
                SELECT COUNT(*) FROM (
                  SELECT n.cutscene_id,n.language
                  FROM scene_node n
                  GROUP BY n.cutscene_id,n.language
                  HAVING NOT EXISTS(
                    SELECT 1 FROM scene_edge s
                    WHERE s.cutscene_id=n.cutscene_id AND s.language=n.language
                      AND s.current_node_id IS NULL AND s.next_node_id IS NOT NULL)
                    OR NOT EXISTS(
                    SELECT 1 FROM scene_edge e
                    WHERE e.cutscene_id=n.cutscene_id AND e.language=n.language
                      AND e.current_node_id IS NOT NULL AND e.next_node_id IS NULL)
                )
                """;
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
                throw new InvalidDataException("Official profile pack scene graph has incomplete boundaries");
        }
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        connection.Open();
        return connection;
    }

    private async Task WriteManifestAtomicAsync(RemoteManifest manifest, CancellationToken token)
    {
        var partial = LocalManifestPath + ".partial";
        try
        {
            await File.WriteAllTextAsync(partial, JsonSerializer.Serialize(manifest, JsonOptions()), token)
                .ConfigureAwait(false);
            File.Move(partial, LocalManifestPath, true);
        }
        finally { TryDelete(partial); }
    }

    private static async Task CopyBoundedAsync(
        Stream input, Stream output, long expectedLength, CancellationToken token)
    {
        if (expectedLength <= 0 || expectedLength > MaximumArchiveBytes)
            throw new InvalidDataException("Official profile pack length is invalid");
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            copied = checked(copied + read);
            if (copied > expectedLength) throw new InvalidDataException("Official profile pack exceeds declared length");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        await output.FlushAsync(token).ConfigureAwait(false);
        if (copied != expectedLength) throw new InvalidDataException("Official profile pack length does not match manifest");
    }

    private static async Task<string> Sha256Async(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static void ValidateManifest(RemoteManifest manifest)
    {
        if (manifest.SchemaVersion is not (1 or CurrentSchemaVersion) || manifest.PackVersion <= 0
            || manifest.Length <= 0 || manifest.Length > MaximumArchiveBytes
            || manifest.CatalogVersion is <= 0
            || manifest.DatabaseLength is <= 0 or > MaximumDatabaseBytes
            || !Uri.TryCreate(manifest.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || !IsSha256(manifest.Sha256) || !IsSha256(manifest.DatabaseSha256))
            throw new InvalidDataException("Official profile pack manifest is invalid");
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string NormalizeToken(string value) => new(value.Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant).ToArray());
    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        gate.Dispose();
        if (ownsHttp) http.Dispose();
    }

    private sealed record FallbackProfile(
        string DomainId, string VariantId, string? Sex, string? Age,
        string Language, string ModelHash, string Transcript,
        float[] Embedding, int[] Codes, int RvqLength, int Codebooks, string ProfileHash);

    private sealed record RemoteManifest(int SchemaVersion, int PackVersion, string Url,
        long Length, string Sha256, string DatabaseSha256,
        int? CatalogVersion = null, long? DatabaseLength = null,
        int? ProfileCount = null);
    private sealed record PackProfile(string GroupId, string Label, string ActorToken,
        string Language, string ModelHash, string Transcript, float[] Embedding, int[] Codes,
        int RvqLength, int Codebooks, string SourceMetadata, string ProfileHash);
}
