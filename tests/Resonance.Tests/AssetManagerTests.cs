using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Resonance.Bootstrap;

namespace Resonance.Tests;

public sealed class AssetManagerTests
{
    [Fact]
    public async Task ResumesPartialAndRejectsSameLengthCorruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var expected = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            var artifact = new AssetArtifact("test", "model.bin", new Uri("https://example.invalid/model.bin"),
                expected.Length, Convert.ToHexStringLower(SHA256.HashData(expected)), 5, "test", 0);
            await File.WriteAllBytesAsync(Path.Combine(root, "model.bin.part"), expected[..7], TestContext.Current.CancellationToken);
            var handler = new RangeHandler(expected);
            using var http = new HttpClient(handler);
            using var manager = new AssetManager(root, http);

            var path = await manager.EnsureAsync(artifact, TestContext.Current.CancellationToken);

            Assert.Equal(7, handler.RequestedOffset);
            Assert.Equal(expected, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            await File.WriteAllBytesAsync(path, new byte[expected.Length], TestContext.Current.CancellationToken);
            Assert.False(await AssetManager.VerifyAsync(path, artifact, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RangeHandler(byte[] content) : HttpMessageHandler
    {
        public long? RequestedOffset { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedOffset = request.Headers.Range?.Ranges.Single().From;
            var offset = checked((int)(RequestedOffset ?? 0));
            var response = new HttpResponseMessage(offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content[offset..]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, content.Length - 1, content.Length);
            return Task.FromResult(response);
        }
    }
}
