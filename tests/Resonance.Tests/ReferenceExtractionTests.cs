using System.Text.Json;
using Resonance.Bootstrap;
using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class ReferenceExtractionTests
{
    [Fact]
    public async Task HelperResponseIsConsumedAndTransientFilesAreRemoved()
    {
        var root = CreateRoot();
        try
        {
            var runtimePath = Path.Combine(root, "runtime");
            var talkerPath = Path.Combine(root, "talker.gguf");
            var codecPath = Path.Combine(root, "codec.gguf");
            var helperPath = Path.Combine(root, "ReferenceExtractor.exe");
            Directory.CreateDirectory(runtimePath);
            await File.WriteAllTextAsync(talkerPath, "talker", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(codecPath, "codec", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(helperPath, "test-helper", TestContext.Current.CancellationToken);
            var runner = new WritingRunner();
            await using var manager = new RuntimeManager(new Configuration(), () => { }, talkerPath, codecPath,
                "base", "design", "runtime", runtimePath, helperPath,
                Path.Combine(root, "work"), runner);
            SetSelection(manager, new BackendInfo("cpu", "CPU", BackendType.Cpu, 0, 1, 1));

            var reference = await manager.ExtractReferenceAsync(
                Enumerable.Repeat(0.1f, ReferenceExtractionProtocol.SampleRate).ToArray(),
                "A stable reference sentence.", TestContext.Current.CancellationToken);

            Assert.Equal("A stable reference sentence.", reference.Transcript);
            Assert.Equal(1, runner.InvocationCount);
            AssertNoReferenceTransientArtifacts(Path.Combine(root, "work"));
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public async Task CancelledHelperLeavesNoResponseOrPartialFiles()
    {
        var root = CreateRoot();
        try
        {
            var runtimePath = Path.Combine(root, "runtime");
            var talkerPath = Path.Combine(root, "talker.gguf");
            var codecPath = Path.Combine(root, "codec.gguf");
            var helperPath = Path.Combine(root, "ReferenceExtractor.exe");
            Directory.CreateDirectory(runtimePath);
            await File.WriteAllTextAsync(talkerPath, "talker", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(codecPath, "codec", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(helperPath, "test-helper", TestContext.Current.CancellationToken);
            var runner = new BlockingRunner();
            await using var manager = new RuntimeManager(new Configuration(), () => { }, talkerPath, codecPath,
                "base", "design", "runtime", runtimePath, helperPath,
                Path.Combine(root, "work"), runner);
            SetSelection(manager, new BackendInfo("cpu", "CPU", BackendType.Cpu, 0, 1, 1));
            using var cancellation = new CancellationTokenSource();
            var extraction = manager.ExtractReferenceAsync(
                Enumerable.Repeat(0.1f, ReferenceExtractionProtocol.SampleRate).ToArray(),
                "A cancellable reference sentence.", cancellation.Token).AsTask();
            await runner.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extraction);
            AssertNoReferenceTransientArtifacts(Path.Combine(root, "work"));
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public async Task AbandonedHelperKeepsBaseInferenceStoppedUntilRestart()
    {
        var root = CreateRoot();
        try
        {
            var runtimePath = Path.Combine(root, "runtime");
            var talkerPath = Path.Combine(root, "talker.gguf");
            var codecPath = Path.Combine(root, "codec.gguf");
            var helperPath = Path.Combine(root, "ReferenceExtractor.exe");
            Directory.CreateDirectory(runtimePath);
            await File.WriteAllTextAsync(talkerPath, "talker", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(codecPath, "codec", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(helperPath, "test-helper", TestContext.Current.CancellationToken);
            await using var manager = new RuntimeManager(new Configuration(), () => { }, talkerPath, codecPath,
                "base", "design", "runtime", runtimePath, helperPath,
                Path.Combine(root, "work"), new AbandonedRunner());
            SetSelection(manager, new BackendInfo("cpu", "CPU", BackendType.Cpu, 0, 1, 1));

            await Assert.ThrowsAsync<ReferenceExtractionProcessException>(() => manager.ExtractReferenceAsync(
                Enumerable.Repeat(0.1f, ReferenceExtractionProtocol.SampleRate).ToArray(),
                "An abandoned reference.", TestContext.Current.CancellationToken).AsTask());
            Assert.True(manager.HasNativeOwnershipFailure);
            Assert.False(manager.IsReady);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ExtractReferenceAsync(
                Enumerable.Repeat(0.1f, ReferenceExtractionProtocol.SampleRate).ToArray(),
                "A second reference.", TestContext.Current.CancellationToken).AsTask());
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public void ProtocolRejectsInputOutputAliasAndUnknownResponseMembers()
    {
        var root = CreateRoot();
        try
        {
            var runtimePath = Path.Combine(root, "runtime");
            var inputPath = Path.Combine(root, "input.f32");
            var talkerPath = Path.Combine(root, "talker.gguf");
            var codecPath = Path.Combine(root, "codec.gguf");
            Directory.CreateDirectory(runtimePath);
            File.WriteAllBytes(inputPath, new byte[sizeof(float)]);
            File.WriteAllText(talkerPath, "talker");
            File.WriteAllText(codecPath, "codec");
            var request = new ReferenceExtractionRequest(
                ReferenceExtractionProtocol.SchemaVersion,
                ReferenceExtractionProtocol.AbiVersion,
                runtimePath, talkerPath, codecPath, "cpu", inputPath, inputPath, "text",
                runtimePath, root, root, Guid.NewGuid().ToString("N"), root);

            Assert.Throws<InvalidDataException>(() =>
                ReferenceExtractionProtocol.ValidateRequest(request, requireInput: true));
            var json = "{\"schemaVersion\":1,\"abi\":6,\"speakerDimension\":1,\"referenceLength\":1,\"codebooks\":1,\"transcript\":\"text\",\"speakerEmbedding\":[0],\"rvqCodes\":[0],\"extra\":true}";
            Assert.Throws<JsonException>(() => ReferenceExtractionProtocol.ParseResponse(json, "text"));
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public void ProtocolRejectsPathsOutsideExplicitTrustedRoots()
    {
        var root = CreateRoot();
        try
        {
            var runtimePath = Path.Combine(root, "runtime");
            var modelPath = Path.Combine(root, "models");
            var referencePath = Path.Combine(root, "reference");
            Directory.CreateDirectory(runtimePath);
            Directory.CreateDirectory(modelPath);
            Directory.CreateDirectory(referencePath);
            var talkerPath = Path.Combine(root, "outside-talker.gguf");
            var codecPath = Path.Combine(modelPath, "codec.gguf");
            var inputPath = Path.Combine(referencePath, "input.f32");
            var outputPath = Path.Combine(referencePath, "output.json");
            File.WriteAllText(talkerPath, "talker");
            File.WriteAllText(codecPath, "codec");
            File.WriteAllBytes(inputPath, new byte[sizeof(float)]);
            var request = new ReferenceExtractionRequest(
                ReferenceExtractionProtocol.SchemaVersion,
                ReferenceExtractionProtocol.AbiVersion,
                runtimePath, talkerPath, codecPath, "cpu", inputPath, outputPath, "text",
                runtimePath, modelPath, referencePath, Guid.NewGuid().ToString("N"), root);

            Assert.Throws<InvalidDataException>(() =>
                ReferenceExtractionProtocol.ValidateRequest(request, requireInput: true));
        }
        finally { TestDirectory.Delete(root); }
    }

    private static void SetSelection(RuntimeManager manager, BackendInfo backend)
    {
        typeof(RuntimeManager).GetProperty(nameof(RuntimeManager.Selection))!
            .SetValue(manager, new BackendSelection(backend, backend, false, null));
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-reference-extraction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void AssertNoReferenceTransientArtifacts(string workDirectory)
    {
        var files = System.IO.Directory.EnumerateFiles(workDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .ToArray();
        Assert.DoesNotContain(files, name => name!.Equals("input.f32", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, name => name!.Equals("request.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, name => name!.Equals("response.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, name => name!.Equals("owner.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, name => name!.Equals("owner.json.pending", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, name => name!.Equals("launch.ready", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, name => name!.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class WritingRunner : IReferenceExtractionProcessRunner
    {
        public int InvocationCount { get; private set; }

        public async Task<ReferenceExtractionProcessResult> RunAsync(
            string executablePath, string requestPath, CancellationToken token)
        {
            InvocationCount++;
            var request = JsonSerializer.Deserialize<ReferenceExtractionRequest>(
                await File.ReadAllTextAsync(requestPath, token), ReferenceExtractionProtocol.JsonOptions())!;
            var response = new ReferenceExtractionResponse(
                ReferenceExtractionProtocol.SchemaVersion,
                ReferenceExtractionProtocol.AbiVersion,
                1, 1, 1, request.Transcript, [0.25f], [1]);
            await File.WriteAllTextAsync(request.OutputPath,
                JsonSerializer.Serialize(response, ReferenceExtractionProtocol.JsonOptions()), token);
            return new(0, string.Empty);
        }
    }

    private sealed class BlockingRunner : IReferenceExtractionProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ReferenceExtractionProcessResult> RunAsync(
            string executablePath, string requestPath, CancellationToken token)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(0, string.Empty);
        }
    }

    private sealed class AbandonedRunner : IReferenceExtractionProcessRunner
    {
        public Task<ReferenceExtractionProcessResult> RunAsync(
            string executablePath, string requestPath, CancellationToken token) =>
            Task.FromException<ReferenceExtractionProcessResult>(
                new ReferenceExtractionProcessException(-1, "test helper tree is still running", processMayBeRunning: true));
    }
}
