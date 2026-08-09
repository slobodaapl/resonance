using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Resonance.Bootstrap;

public sealed class AssetManager : IDisposable
{
    private readonly string assetDirectory;
    private readonly HttpClient http;
    private readonly bool ownsClient;

    public event Action<AssetProgress>? Progress;

    public AssetManager(string assetDirectory, HttpClient? httpClient = null)
    {
        this.assetDirectory = assetDirectory;
        Directory.CreateDirectory(assetDirectory);
        ownsClient = httpClient is null;
        http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Resonance-Dalamud/0.1");
    }

    public static async Task<AssetManifest> LoadManifestAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AssetManifest>(stream, cancellationToken: token).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Empty asset manifest: {path}");
    }

    public string PathFor(AssetArtifact artifact) => Path.Combine(assetDirectory, artifact.FileName);

    public async Task<string> EnsureAsync(AssetArtifact artifact, CancellationToken token)
    {
        ValidateManifestEntry(artifact);
        var finalPath = PathFor(artifact);
        if (await VerifyAsync(finalPath, artifact, token).ConfigureAwait(false)) return finalPath;

        var partialPath = finalPath + ".part";
        var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (offset >= artifact.Length)
        {
            File.Delete(partialPath);
            offset = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.Url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

        if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            offset = 0;
            File.Delete(partialPath);
        }
        response.EnsureSuccessStatusCode();

        var mode = offset == 0 ? FileMode.CreateNew : FileMode.Append;
        await using (var output = new FileStream(partialPath, mode, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                var received = offset;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;
                    if (received > artifact.Length) throw new InvalidDataException($"{artifact.Id} exceeded declared length");
                    Progress?.Invoke(new(artifact.Id, received, artifact.Length));
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        if (!await VerifyAsync(partialPath, artifact, token).ConfigureAwait(false))
            throw new InvalidDataException($"Downloaded asset failed verification: {artifact.Id}");

        File.Move(partialPath, finalPath, true);
        return finalPath;
    }

    public static async Task<bool> VerifyAsync(string path, AssetArtifact artifact, CancellationToken token)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != artifact.Length) return false;
        await using var stream = file.OpenRead();
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(artifact.Sha256));
    }

    private static void ValidateManifestEntry(AssetArtifact artifact)
    {
        if (!artifact.Url.IsAbsoluteUri || artifact.Url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"Asset URL must use HTTPS: {artifact.Id}");
        if (artifact.Length <= 0 || artifact.Sha256.Length != 64)
            throw new InvalidDataException($"Invalid length/hash: {artifact.Id}");
        if (Path.GetFileName(artifact.FileName) != artifact.FileName)
            throw new InvalidDataException($"Asset filename escapes destination: {artifact.Id}");
    }

    public void Dispose()
    {
        if (ownsClient) http.Dispose();
    }
}
