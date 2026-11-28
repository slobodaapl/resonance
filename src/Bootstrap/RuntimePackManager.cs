using System.IO.Compression;
using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Resonance.Tts;

namespace Resonance.Bootstrap;

public sealed record RuntimePackManifest(int SchemaVersion, int Abi, IReadOnlyList<RuntimePackArtifact> Artifacts);
public sealed record RuntimePackArtifact(string Id, string Url, long Size, string Sha256,
    IReadOnlyList<string> MatchDescriptionContains, IReadOnlyList<string> Files);

public sealed class RuntimePackManager : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsClient;
    private readonly string installRoot;
    private readonly string downloadDirectory;
    public event Action<AssetProgress>? Progress;

    public RuntimePackManager(string installRoot, string downloadDirectory, HttpClient? httpClient = null)
    {
        this.installRoot = Path.GetFullPath(installRoot);
        this.downloadDirectory = downloadDirectory;
        Directory.CreateDirectory(this.installRoot);
        Directory.CreateDirectory(downloadDirectory);
        ownsClient = httpClient is null;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    public async Task EnsureMatchingAsync(RuntimePackManifest manifest, IReadOnlyList<BackendInfo> detected,
        CancellationToken token, bool cudaDriverAvailable = true, Task? installCudaAfter = null)
    {
        var installDirectory = GetInstallDirectory(manifest);
        foreach (var artifact in manifest.Artifacts)
        {
            var isCuda = artifact.Id.Equals("cuda", StringComparison.OrdinalIgnoreCase);
            if (IsCore(artifact)) continue;
            if (isCuda && !cudaDriverAvailable) continue;
            if (!isCuda && !detected.Any(backend => artifact.MatchDescriptionContains.Any(value =>
                    backend.Description.Contains(value, StringComparison.OrdinalIgnoreCase)))) continue;
            await EnsureAsync(installDirectory, artifact, token, isCuda ? installCudaAfter : null).ConfigureAwait(false);
        }
    }

    public async Task<string> EnsureCoreAsync(RuntimePackManifest manifest, CancellationToken token) =>
        await TryEnsureCoreAsync(manifest, token).ConfigureAwait(false)
        ?? throw new InvalidDataException("Runtime manifest has no required core pack");

    public async Task<string?> TryEnsureCoreAsync(RuntimePackManifest manifest, CancellationToken token)
    {
        var core = manifest.Artifacts.SingleOrDefault(IsCore);
        if (core is null) return null;
        if (!core.Files.Contains("qwen.dll", StringComparer.OrdinalIgnoreCase)
            || !core.Files.Contains("ggml.dll", StringComparer.OrdinalIgnoreCase)
            || !core.Files.Contains("ggml-base.dll", StringComparer.OrdinalIgnoreCase)
            || !core.Files.Any(file => file.StartsWith("ggml-cpu", StringComparison.OrdinalIgnoreCase))
            || !core.Files.Any(file => file.StartsWith("ggml-vulkan", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Core runtime pack is missing qwen, CPU, or Vulkan components");
        var installDirectory = GetInstallDirectory(manifest);
        await EnsureAsync(installDirectory, core, token).ConfigureAwait(false);
        return installDirectory;
    }

    private string GetInstallDirectory(RuntimePackManifest manifest)
    {
        if (!manifest.Artifacts.Any(IsCore)) return installRoot;
        // Backends and core DLLs must come from the same runtime pack set.
        var packs = manifest.Artifacts.OrderBy(artifact => artifact.Id, StringComparer.Ordinal)
            .Select(artifact => new { artifact.Id, artifact.Sha256 });
        var identity = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(packs)));
        return Path.Combine(installRoot, identity);
    }

    private static bool IsCore(RuntimePackArtifact artifact) =>
        artifact.Id.Equals("core", StringComparison.OrdinalIgnoreCase);

    internal static async Task<RuntimePackManifest> LoadManifestAsync(string manifestPath, CancellationToken token)
    {
        await using var manifestStream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<RuntimePackManifest>(manifestStream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Runtime manifest is empty");
        if (manifest.SchemaVersion != 1 || manifest.Abi != QwenNative.AbiVersion)
            throw new InvalidDataException("Runtime manifest schema or ABI mismatch");
        return manifest;
    }

    private async Task EnsureAsync(string installDirectory, RuntimePackArtifact artifact, CancellationToken token,
        Task? installAfter = null)
    {
        ValidateArtifact(artifact);
        var marker = Path.Combine(installDirectory, $".runtime-{artifact.Id}-{artifact.Sha256}.installed");
        var archive = Path.Combine(downloadDirectory, artifact.Id + ".zip");
        if (File.Exists(marker)
            && await HasExpectedHashAsync(archive, artifact, token).ConfigureAwait(false)
            && await InstalledFilesMatchArchiveAsync(installDirectory, archive, artifact, token).ConfigureAwait(false)) return;
        if (!await HasExpectedHashAsync(archive, artifact, token).ConfigureAwait(false))
            await DownloadAsync(artifact, archive, token).ConfigureAwait(false);

        if (installAfter is not null) await installAfter.WaitAsync(token).ConfigureAwait(false);
        using var zip = ZipFile.OpenRead(archive);
        var entries = zip.Entries.Where(entry => entry.Name.Length > 0).ToArray();
        if (entries.Length != artifact.Files.Count
            || entries.Any(entry => entry.FullName != entry.Name || !artifact.Files.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Runtime pack '{artifact.Id}' contains unexpected files");
        WindowsNativeLibrary.RequireUnloaded(artifact.Files.Select(file => Path.Combine(installDirectory, file)));
        Directory.CreateDirectory(installDirectory);
        foreach (var file in artifact.Files)
        {
            var entry = entries.Single(value => value.Name.Equals(file, StringComparison.OrdinalIgnoreCase));
            var destination = Path.Combine(installDirectory, file);
            var temporary = destination + ".part";
            await using (var input = entry.Open())
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             1024 * 128, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            File.Move(temporary, destination, true);
        }
        await File.WriteAllTextAsync(marker, artifact.Sha256, token).ConfigureAwait(false);
    }

    private static async Task<bool> InstalledFilesMatchArchiveAsync(string installDirectory, string archive, RuntimePackArtifact artifact,
        CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var file in artifact.Files)
        {
            var installed = Path.Combine(installDirectory, file);
            var entry = zip.Entries.SingleOrDefault(value =>
                value.FullName == value.Name && value.Name.Equals(file, StringComparison.OrdinalIgnoreCase));
            if (entry is null || !File.Exists(installed) || new FileInfo(installed).Length != entry.Length) return false;
            await using var installedStream = File.OpenRead(installed);
            await using var archiveStream = entry.Open();
            var installedHash = await SHA256.HashDataAsync(installedStream, token).ConfigureAwait(false);
            var archiveHash = await SHA256.HashDataAsync(archiveStream, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(installedHash, archiveHash)) return false;
        }
        return true;
    }

    private async Task DownloadAsync(RuntimePackArtifact artifact, string archive, CancellationToken token)
    {
        var uri = new Uri(artifact.Url, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Runtime URL must use HTTPS");
        var partial = archive + ".part";
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset >= artifact.Size)
        {
            File.Delete(partial);
            offset = 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        Progress?.Invoke(new($"runtime-{artifact.Id}", offset, artifact.Size));
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            File.Delete(partial);
            offset = 0;
            Progress?.Invoke(new($"runtime-{artifact.Id}", offset, artifact.Size));
        }
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append,
                         FileAccess.Write, FileShare.None, 1024 * 128, true))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 128);
            try
            {
                var received = offset;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;
                    if (received > artifact.Size) throw new InvalidDataException("Runtime pack exceeded declared length");
                    Progress?.Invoke(new($"runtime-{artifact.Id}", received, artifact.Size));
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        if (new FileInfo(partial).Length != artifact.Size) throw new InvalidDataException("Runtime pack length mismatch");
        if (!await HasExpectedHashAsync(partial, artifact, token).ConfigureAwait(false))
            throw new InvalidDataException("Runtime pack SHA-256 mismatch");
        File.Move(partial, archive, true);
    }

    private static async Task<bool> HasExpectedHashAsync(string path, RuntimePackArtifact artifact, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.Size) return false;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(hash), System.Text.Encoding.ASCII.GetBytes(artifact.Sha256.ToLowerInvariant()));
    }

    private static void ValidateArtifact(RuntimePackArtifact artifact)
    {
        if (artifact.Size <= 0 || artifact.Sha256.Length != 64 || artifact.Files.Count == 0)
            throw new InvalidDataException($"Runtime pack '{artifact.Id}' metadata is invalid");
        if (artifact.Files.Any(file => Path.GetFileName(file) != file
                                       || !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Runtime pack '{artifact.Id}' has an unsafe file allowlist");
    }

    public void Dispose()
    {
        if (ownsClient) http.Dispose();
    }
}
