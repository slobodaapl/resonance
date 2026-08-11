using System.Collections.Concurrent;
using Resonance.Audio;
using Resonance.Bootstrap;
using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class RuntimeOwnershipTests
{
    [Theory]
    [InlineData(nameof(RuntimeManager.EnsureReadyAsync))]
    [InlineData(nameof(RuntimeManager.RefreshBackendsAsync))]
    [InlineData(nameof(RuntimeManager.BenchmarkAndApplyAsync))]
    [InlineData(nameof(RuntimeManager.SetDesiredAsync))]
    [InlineData(nameof(RuntimeManager.SynthesizeAsync))]
    public async Task OwnershipPoisonedWhileCallerWaitsStopsNativeEntry(string operation)
    {
        var root = CreateRoot();
        OwnershipRaceSeam? seam = null;
        try
        {
            var runtimePath = Path.Combine(root, "runtime");
            var talkerPath = Path.Combine(root, "talker.gguf");
            var codecPath = Path.Combine(root, "codec.gguf");
            var helperPath = Path.Combine(root, "ReferenceExtractor.exe");
            var designPath = Path.Combine(root, "design.gguf");
            Directory.CreateDirectory(runtimePath);
            await File.WriteAllTextAsync(talkerPath, "talker", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(codecPath, "codec", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(helperPath, "test-helper", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(designPath, "design", TestContext.Current.CancellationToken);

            var backend = new BackendInfo("cpu", "CPU", BackendType.Cpu, 0, 1, 1);
            await using var manager = new RuntimeManager(new Configuration(), () => { }, talkerPath, codecPath,
                "base", "design", "runtime", runtimePath, helperPath,
                Path.Combine(root, "work"), new NoOpRunner());
            manager.SetTestBackendState(new BackendSelection(backend, backend, false, null));
            seam = new OwnershipRaceSeam(operation);
            manager.SetTestSeam(seam);

            var holder = manager.EnsureReadyAsync(TestContext.Current.CancellationToken);
            await seam.HolderAcquired.Task.WaitAsync(TestContext.Current.CancellationToken);
            var caller = StartOperation(manager, operation, backend, designPath,
                TestContext.Current.CancellationToken);
            await seam.CallerWaiting.Task.WaitAsync(TestContext.Current.CancellationToken);

            seam.NativeOwnershipFailure = new InvalidOperationException("injected runtime disposal poison");
            seam.ReleaseHolder.TrySetResult();

            await Assert.ThrowsAsync<InvalidOperationException>(() => holder);
            await Assert.ThrowsAsync<InvalidOperationException>(() => caller);
            Assert.Empty(seam.NativeOperations);
        }
        finally
        {
            seam?.ReleaseHolder.TrySetResult();
            TestDirectory.Delete(root);
        }
    }

    private static Task StartOperation(
        RuntimeManager manager,
        string operation,
        BackendInfo backend,
        string designPath,
        CancellationToken token)
    {
        return operation switch
        {
            nameof(RuntimeManager.EnsureReadyAsync) => manager.EnsureReadyAsync(token),
            nameof(RuntimeManager.RefreshBackendsAsync) => manager.RefreshBackendsAsync(token),
            nameof(RuntimeManager.BenchmarkAndApplyAsync) => manager.BenchmarkAndApplyAsync(designPath, token),
            nameof(RuntimeManager.SetDesiredAsync) => manager.SetDesiredAsync(backend, token),
            nameof(RuntimeManager.SynthesizeAsync) => SynthesizeAsync(manager, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown runtime operation")
        };
    }

    private static async Task SynthesizeAsync(RuntimeManager manager, CancellationToken token)
    {
        using var sink = new StreamingAudioBuffer();
        await manager.SynthesizeAsync(
            new SynthesisRequest("test", "english", null, "test", 1, 32), sink, token);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-runtime-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class OwnershipRaceSeam(string targetOperation) : IRuntimeManagerTestSeam
    {
        private readonly string targetOperation = targetOperation;
        private int acquisitionCount;
        private Exception? nativeOwnershipFailure;

        public TaskCompletionSource HolderAcquired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CallerWaiting { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseHolder { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> NativeOperations { get; } = new();

        public Exception? NativeOwnershipFailure
        {
            get => nativeOwnershipFailure;
            set => nativeOwnershipFailure = value;
        }

        public async Task WaitForBaseOwnershipAsync(
            string operation,
            Func<Task> wait,
            CancellationToken token)
        {
            var acquisition = Interlocked.Increment(ref acquisitionCount);
            var ownership = wait();
            if (acquisition == 1)
            {
                await ownership.ConfigureAwait(false);
                HolderAcquired.TrySetResult();
                await ReleaseHolder.Task.WaitAsync(token).ConfigureAwait(false);
                return;
            }

            if (String.Equals(operation, targetOperation, StringComparison.Ordinal))
                CallerWaiting.TrySetResult();
            await ownership.ConfigureAwait(false);
        }

        public void NativeOperationStarted(string operation) => NativeOperations.Enqueue(operation);
    }

    private sealed class NoOpRunner : IReferenceExtractionProcessRunner
    {
        public Task<ReferenceExtractionProcessResult> RunAsync(
            string executablePath, string requestPath, CancellationToken token) =>
            Task.FromException<ReferenceExtractionProcessResult>(
                new InvalidOperationException("reference runner must not be reached"));
    }
}
