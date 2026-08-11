using System.Text.Json;
using Resonance.Bootstrap;
using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class RuntimeHotLoadTests
{
    [Fact]
    public async Task EnabledHotLoadRestoresAndRetainsBaseUntilShutdown()
    {
        var root = CreateRoot();
        try
        {
            var seam = new HotLoadSeam();
            var manager = CreateManager(root, new Configuration { KeepBaseModelLoaded = true }, seam);
            var safe = false;
            var generation = 1L;
            manager.SetBaseHotLoadSafetyPredicate(() => new BaseHotLoadSafety(safe, generation));

            await manager.EnsureBaseHotLoadedWhenSafeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, seam.EnsureCount);
            safe = true;
            await manager.EnsureBaseHotLoadedWhenSafeAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, seam.EnsureCount);
            Assert.True(seam.BaseRuntimeReady);

            await manager.DisposeAsync();
            Assert.True(seam.Disposed);
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public async Task DisabledHotLoadLeavesBaseLazyAndHelperRestoresEnabledBase()
    {
        var root = CreateRoot();
        try
        {
            var disabledSeam = new HotLoadSeam();
            var disabled = CreateManager(root, new Configuration(), disabledSeam);
            await disabled.EnsureBaseHotLoadedWhenSafeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, disabledSeam.EnsureCount);
            await disabled.DisposeAsync();

            var enabledSeam = new HotLoadSeam { BaseRuntimeReady = true };
            var enabled = CreateManager(root, new Configuration { KeepBaseModelLoaded = true }, enabledSeam);
            var backend = new BackendInfo("cpu", "CPU", BackendType.Cpu, 0, 1, 1);
            enabled.SetTestBackendState(new BackendSelection(backend, backend, false, null));
            await enabled.ExtractReferenceAsync(
                Enumerable.Repeat(0.1f, ReferenceExtractionProtocol.SampleRate).ToArray(),
                "A stable reference sentence.", TestContext.Current.CancellationToken);

            Assert.Equal(1, enabledSeam.ReleaseCount);
            Assert.Equal(1, enabledSeam.EnsureCount);
            Assert.True(enabledSeam.BaseRuntimeReady);
            await enabled.DisposeAsync();
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public async Task HelperCompletionIsPublishedBeforeResidentBaseRestoreFinishes()
    {
        var root = CreateRoot();
        try
        {
            var seam = new HotLoadSeam { BaseRuntimeReady = true, BlockEnsure = true };
            var manager = CreateManager(root, new Configuration { KeepBaseModelLoaded = true }, seam);
            var backend = new BackendInfo("cpu", "CPU", BackendType.Cpu, 0, 1, 1);
            manager.SetTestBackendState(new BackendSelection(backend, backend, false, null));

            await manager.ExtractReferenceAsync(
                Enumerable.Repeat(0.1f, ReferenceExtractionProtocol.SampleRate).ToArray(),
                "A stable reference sentence.", TestContext.Current.CancellationToken);

            Assert.Equal(1, seam.EnsureCount);
            seam.EnsureRelease.TrySetResult();
            await manager.DisposeAsync();
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public async Task UnsafeGenerationCancelsPausedHotLoadBeforeItPublishes()
    {
        var root = CreateRoot();
        try
        {
            var seam = new HotLoadSeam { BlockEnsure = true };
            var manager = CreateManager(root, new Configuration { KeepBaseModelLoaded = true }, seam);
            var safe = true;
            var generation = 1L;
            manager.SetBaseHotLoadSafetyPredicate(() => new BaseHotLoadSafety(safe, generation));

            var restore = manager.EnsureBaseHotLoadedWhenSafeAsync(TestContext.Current.CancellationToken);
            await seam.EnsureStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            safe = false;
            generation++;
            manager.CancelBaseHotLoadRestore();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
            Assert.False(seam.BaseRuntimeReady);
            await manager.DisposeAsync();
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public async Task DisablingHotLoadAwaitsAndUnloadsResidentBase()
    {
        var root = CreateRoot();
        try
        {
            var configuration = new Configuration { KeepBaseModelLoaded = true };
            var seam = new HotLoadSeam { BaseRuntimeReady = true };
            var manager = CreateManager(root, configuration, seam);

            await manager.SetBaseHotLoadEnabledAsync(false, TestContext.Current.CancellationToken);

            Assert.False(configuration.KeepBaseModelLoaded);
            Assert.True(seam.Disposed);
            Assert.False(seam.BaseRuntimeReady);
            await manager.DisposeAsync();
        }
        finally { TestDirectory.Delete(root); }
    }

    private static RuntimeManager CreateManager(string root, Configuration configuration, HotLoadSeam seam)
    {
        var runtimePath = Path.Combine(root, Guid.NewGuid().ToString("N"), "runtime");
        var workPath = Path.Combine(root, Guid.NewGuid().ToString("N"), "work");
        Directory.CreateDirectory(runtimePath);
        Directory.CreateDirectory(workPath);
        var talkerPath = Path.Combine(root, "talker-" + Guid.NewGuid().ToString("N") + ".gguf");
        var codecPath = Path.Combine(root, "codec-" + Guid.NewGuid().ToString("N") + ".gguf");
        var helperPath = Path.Combine(root, "ReferenceExtractor-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(talkerPath, "talker");
        File.WriteAllText(codecPath, "codec");
        File.WriteAllText(helperPath, "test-helper");
        var manager = new RuntimeManager(configuration, () => { }, talkerPath, codecPath,
            "base", "design", "runtime", runtimePath, helperPath, workPath,
            new WritingRunner());
        manager.SetTestSeam(seam);
        return manager;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-hot-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class HotLoadSeam : IRuntimeManagerTestSeam
    {
        private int baseRuntimeReady;

        public Exception? NativeOwnershipFailure => null;
        public int EnsureCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public bool Disposed { get; private set; }
        public bool BlockEnsure { get; init; }
        public TaskCompletionSource EnsureStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EnsureRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BaseRuntimeReady
        {
            get => Volatile.Read(ref baseRuntimeReady) != 0;
            set => Volatile.Write(ref baseRuntimeReady, value ? 1 : 0);
        }

        public async Task EnsureBaseRuntimeAsync(Func<CancellationToken, Task> _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            EnsureCount++;
            EnsureStarted.TrySetResult();
            if (BlockEnsure)
                await EnsureRelease.Task.WaitAsync(token);
            BaseRuntimeReady = true;
        }

        public void BaseRuntimeReleased()
        {
            ReleaseCount++;
            BaseRuntimeReady = false;
        }

        public void BaseRuntimeDisposed()
        {
            Disposed = true;
            BaseRuntimeReady = false;
        }

        public Task WaitForBaseOwnershipAsync(string operation, Func<Task> wait, CancellationToken token) => wait();

        public void NativeOperationStarted(string operation) { }
    }

    private sealed class WritingRunner : IReferenceExtractionProcessRunner
    {
        public async Task<ReferenceExtractionProcessResult> RunAsync(
            string executablePath, string requestPath, CancellationToken token)
        {
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
}
