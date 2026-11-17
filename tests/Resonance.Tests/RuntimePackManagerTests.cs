using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Headers;
using Resonance.Bootstrap;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class RuntimePackManagerTests
{
    [Fact]
    public async Task EmptyManifestIsANoOp()
    {
        var root = CreateRoot();
        try
        {
            var manifest = Path.Combine(root, "runtimes.json");
            await File.WriteAllTextAsync(manifest,
                $"{{\"schemaVersion\":1,\"abi\":{QwenNative.AbiVersion},\"artifacts\":[]}}",
                TestContext.Current.CancellationToken);
            using var manager = new RuntimePackManager(root, Path.Combine(root, "downloads"));
            var document = await RuntimePackManager.LoadManifestAsync(manifest, TestContext.Current.CancellationToken);
            Assert.Null(await manager.TryEnsureCoreAsync(document, TestContext.Current.CancellationToken));
            await manager.EnsureMatchingAsync(document, [], TestContext.Current.CancellationToken);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingRequiredCorePackFailsExplicitly()
    {
        var root = CreateRoot();
        try
        {
            var manifest = Path.Combine(root, "runtimes.json");
            await File.WriteAllTextAsync(manifest,
                $"{{\"schemaVersion\":1,\"abi\":{QwenNative.AbiVersion},\"artifacts\":[]}}",
                TestContext.Current.CancellationToken);
            using var manager = new RuntimePackManager(root, Path.Combine(root, "downloads"));

            var document = await RuntimePackManager.LoadManifestAsync(manifest, TestContext.Current.CancellationToken);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                manager.EnsureCoreAsync(document, TestContext.Current.CancellationToken));
            Assert.Contains("required core", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task VerifiedCorePackExtractsOnlyDeclaredRuntimeDlls()
    {
        var root = CreateRoot();
        try
        {
            var downloads = Path.Combine(root, "downloads");
            Directory.CreateDirectory(downloads);
            var archive = Path.Combine(downloads, "core.zip");
            string[] files = ["qwen.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll", "ggml-vulkan.dll"];
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                foreach (var file in files)
                    using (var writer = new StreamWriter(zip.CreateEntry(file).Open())) writer.Write(file);
            var bytes = await File.ReadAllBytesAsync(archive, TestContext.Current.CancellationToken);
            File.Delete(archive);
            await File.WriteAllBytesAsync(archive + ".part", bytes[..11], TestContext.Current.CancellationToken);
            var artifact = new RuntimePackArtifact("core", "https://example.invalid/core.zip", bytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), [], files);
            var manifest = Path.Combine(root, "runtimes.json");
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new RuntimePackManifest(
                1, QwenNative.AbiVersion, [artifact])), TestContext.Current.CancellationToken);
            var handler = new RangeHandler(bytes);
            using var http = new HttpClient(handler);
            using var manager = new RuntimePackManager(root, downloads, http);
            var progress = new List<AssetProgress>();
            manager.Progress += progress.Add;

            var document = await RuntimePackManager.LoadManifestAsync(manifest, TestContext.Current.CancellationToken);
            var installed = await manager.EnsureCoreAsync(document, TestContext.Current.CancellationToken);

            Assert.Equal(11, handler.RequestedOffset);
            Assert.Equal(11, progress[0].BytesReceived);
            Assert.Equal(bytes.LongLength, progress[^1].BytesReceived);
            Assert.All(progress, value => Assert.Equal("runtime-core", value.Id));
            Assert.All(files, file => Assert.True(File.Exists(Path.Combine(installed, file))));

            await File.WriteAllTextAsync(Path.Combine(installed, "qwen.dll"), "corrupt",
                TestContext.Current.CancellationToken);
            Assert.Equal(installed, await manager.EnsureCoreAsync(document, TestContext.Current.CancellationToken));
            Assert.Equal("qwen.dll", await File.ReadAllTextAsync(Path.Combine(installed, "qwen.dll"),
                TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RuntimeUpdatePreservesInUsePacksAndKeepsBackendsWithTheirCore()
    {
        var root = CreateRoot();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var downloads = Path.Combine(root, "downloads");
            using var manager = new RuntimePackManager(root, downloads);
            var legacyDll = Path.Combine(root, "ggml-base.dll");
            await File.WriteAllTextAsync(legacyDll, "legacy", token);
            using var legacyLock = File.OpenRead(legacyDll);

            string[] coreDlls = ["qwen.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll", "ggml-vulkan.dll"];
            var core = WriteCachedPack(downloads, "core", "first core", coreDlls);
            var cuda = WriteCachedPack(downloads, "cuda", "first CUDA", ["ggml-cuda.dll"]);
            var first = new RuntimePackManifest(1, QwenNative.AbiVersion, [core, cuda]);
            var previous = await manager.EnsureCoreAsync(first, token);
            await manager.EnsureMatchingAsync(first, [], token);
            using var previousLock = File.OpenRead(Path.Combine(previous, "ggml-base.dll"));

            // A backend-only update also needs its own directory, even when core bytes match.
            var newCuda = WriteCachedPack(downloads, "cuda", "new CUDA", ["ggml-cuda.dll"]);
            var second = new RuntimePackManifest(1, QwenNative.AbiVersion, [core, newCuda]);
            var updated = await manager.EnsureCoreAsync(second, token);
            await manager.EnsureMatchingAsync(second, [], token);

            Assert.Equal("legacy", await File.ReadAllTextAsync(legacyDll, token));
            Assert.Equal("first core", await File.ReadAllTextAsync(Path.Combine(previous, "ggml-base.dll"), token));
            Assert.Equal("first CUDA", await File.ReadAllTextAsync(Path.Combine(previous, "ggml-cuda.dll"), token));
            Assert.Equal("first core", await File.ReadAllTextAsync(Path.Combine(updated, "ggml-base.dll"), token));
            Assert.Equal("new CUDA", await File.ReadAllTextAsync(Path.Combine(updated, "ggml-cuda.dll"), token));
            Assert.Equal(updated, await manager.EnsureCoreAsync(second, token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnsafeAllowlistIsRejectedBeforeDownload()
    {
        var root = CreateRoot();
        try
        {
            var document = new RuntimePackManifest(1, QwenNative.AbiVersion,
            [
                new("cuda", "https://example.invalid/runtime.zip", 1, new string('a', 64),
                    ["NVIDIA"], ["../escape.dll"]),
            ]);
            using var manager = new RuntimePackManager(root, Path.Combine(root, "downloads"));
            var backend = new BackendInfo("Vulkan0", "NVIDIA GPU", BackendType.Vulkan, 0, 0, 0);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                manager.EnsureMatchingAsync(document, [backend], TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CudaPackIsIgnoredWhenCudaDriverBridgeIsUnavailable()
    {
        var root = CreateRoot();
        try
        {
            var document = new RuntimePackManifest(1, QwenNative.AbiVersion,
            [
                new("cuda", "https://example.invalid/runtime.zip", 1, new string('a', 64),
                    ["NVIDIA"], ["ggml-cuda.dll"]),
            ]);
            using var manager = new RuntimePackManager(root, Path.Combine(root, "downloads"));
            var backend = new BackendInfo("Vulkan0", "NVIDIA GPU", BackendType.Vulkan, 0, 0, 0);
            await manager.EnsureMatchingAsync(document, [backend], TestContext.Current.CancellationToken,
                cudaDriverAvailable: false);
            Assert.False(Directory.EnumerateFiles(Path.Combine(root, "downloads")).Any());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CudaBridgeSelectsPackWithoutDependingOnVulkanVendorText()
    {
        var root = CreateRoot();
        try
        {
            var document = new RuntimePackManifest(1, QwenNative.AbiVersion,
            [
                new("cuda", "https://example.invalid/runtime.zip", 1, new string('a', 64),
                    ["NVIDIA"], ["../escape.dll"]),
            ]);
            using var manager = new RuntimePackManager(root, Path.Combine(root, "downloads"));

            await Assert.ThrowsAsync<InvalidDataException>(() => manager.EnsureMatchingAsync(
                document, [], TestContext.Current.CancellationToken, cudaDriverAvailable: true));
        }
        finally { Directory.Delete(root, true); }
    }

    private static RuntimePackArtifact WriteCachedPack(string downloads, string id, string content, string[] files)
    {
        var archive = Path.Combine(downloads, id + ".zip");
        File.Delete(archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            foreach (var file in files)
                using (var writer = new StreamWriter(zip.CreateEntry(file).Open())) writer.Write(content);
        var bytes = File.ReadAllBytes(archive);
        return new(id, $"https://example.invalid/{id}.zip", bytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), [], files);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-runtime-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
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
