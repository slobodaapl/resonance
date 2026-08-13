using Resonance.Audio;
using Resonance.Bootstrap;
using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class RuntimeBenchmarkIsolationTests
{
    [Fact]
    public async Task ManualPluginBenchmarkUsesDesignModelInDisposableHelper()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-benchmark-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new Configuration { Compute = ComputePreference.Manual };
            var backend = new BackendInfo("CUDA0", "GPU", BackendType.Accelerator, 0, 8, 24);
            var designPath = Path.Combine(root, "design.gguf");
            var host = new BenchmarkHost(backend);
            var saves = 0;
            string? helperModelPath = null;
            await using var manager = new RuntimeManager(configuration, () => saves++,
                Path.Combine(root, "talker.gguf"), Path.Combine(root, "codec.gguf"),
                "base", "design", "runtime", Path.Combine(root, "runtime"),
                Path.Combine(root, "ReferenceExtractor.exe"), Path.Combine(root, "work"));
            manager.SetTestBackendState(new BackendSelection(backend, backend, false, null));
            manager.SetBaseHostFactory((_, modelPath, _, _, _, _) =>
            {
                helperModelPath = modelPath;
                return host;
            });

            await manager.BenchmarkAndApplyAsync(designPath, TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath(designPath), Path.GetFullPath(helperModelPath!));
            Assert.Equal(backend.Name, host.StartedBackend);
            Assert.Equal([backend], host.BenchmarkedBackends);
            Assert.Equal(1, host.DisposeCount);
            Assert.Equal(backend.Name, configuration.BackendBenchmark?.WinnerName);
            Assert.Equal(1, saves);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FailedBackendHelperIsDisposedAndNextBackendStillRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-benchmark-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new Configuration { Compute = ComputePreference.Manual };
            var cuda = new BackendInfo("CUDA0", "GPU", BackendType.Accelerator, 0, 8, 24);
            var cpu = new BackendInfo("CPU", "CPU", BackendType.Cpu, 0, 32, 64);
            var failed = new BenchmarkHost(cuda) { StartFailure = new InvalidOperationException("helper terminated") };
            var passed = new BenchmarkHost(cpu);
            var hosts = new Queue<IBaseRuntimeHost>([failed, passed]);
            await using var manager = new RuntimeManager(configuration, () => { },
                Path.Combine(root, "talker.gguf"), Path.Combine(root, "codec.gguf"),
                "base", "design", "runtime", Path.Combine(root, "runtime"),
                Path.Combine(root, "ReferenceExtractor.exe"), Path.Combine(root, "work"));
            manager.SetTestBackendState(new BackendSelection(cuda, cuda, false, null), [cuda, cpu]);
            manager.SetBaseHostFactory((_, _, _, _, _, _) => hosts.Dequeue());

            await manager.BenchmarkAndApplyAsync(
                Path.Combine(root, "design.gguf"), TestContext.Current.CancellationToken);

            Assert.Equal(1, failed.DisposeCount);
            Assert.Equal(1, passed.DisposeCount);
            Assert.Equal(cpu.Name, configuration.BackendBenchmark?.WinnerName);
            Assert.Collection(configuration.BackendBenchmark!.Measurements,
                result =>
                {
                    Assert.Equal(cuda.Name, result.BackendName);
                    Assert.False(result.Successful);
                    Assert.Contains("helper terminated", result.Error);
                },
                result =>
                {
                    Assert.Equal(cpu.Name, result.BackendName);
                    Assert.True(result.Successful);
                });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class BenchmarkHost(BackendInfo backend) : IBaseRuntimeHost
    {
        public bool IsReady => StartedBackend is not null;
        public bool ContextReady => IsReady;
        public string? ActiveBackendId => StartedBackend;
        public bool IsBusy => false;
        public string? StartedBackend { get; private set; }
        public IReadOnlyList<BackendInfo> BenchmarkedBackends { get; private set; } = [];
        public int DisposeCount { get; private set; }
        public Exception? StartFailure { get; init; }

        public Task StartAsync(string backendName, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (StartFailure is not null) throw StartFailure;
            StartedBackend = backendName;
            return Task.CompletedTask;
        }

        public Task SwitchBackendAsync(string backendName, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<VoiceReference> ExtractReferenceAsync(
            ReadOnlyMemory<float> samples, string transcript, CancellationToken token) =>
            throw new NotSupportedException();

        public Task SynthesizeAsync(
            SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BackendBenchmarkMeasurement>> BenchmarkAsync(
            IReadOnlyList<BackendInfo> backends, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            BenchmarkedBackends = backends;
            return Task.FromResult<IReadOnlyList<BackendBenchmarkMeasurement>>([
                new(backend.Name, true, 0.1, 0.2, 0.3, null),
            ]);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
