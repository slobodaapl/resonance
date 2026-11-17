using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Resonance.Bootstrap;
using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class LiveQualitySwitchTests
{
    [Fact]
    public async Task PreparationKeepsCurrentRuntimeAndActivationNeverOverlapsModelOwners()
    {
        await using var fixture = new Fixture();
        fixture.Configuration.Compute = ComputePreference.Manual;
        fixture.Configuration.DesiredBackendName = "cpu";
        var old = await fixture.Activate(QualityPreset.Q4Base06B);
        var prepared = await fixture.Bootstrap.PrepareQualityAsync(QualityPreset.Q8Base17B, TestContext.Current.CancellationToken);
        Assert.Same(old, fixture.Bootstrap.RuntimeManager);
        Assert.True(old.IsReady);
        Assert.Equal(1, fixture.LiveModels);
        Assert.EndsWith("base-1.7b-q8.gguf", prepared.BasePath);
        Assert.EndsWith("tokenizer-q8.gguf", prepared.CodecPath);
        await fixture.Bootstrap.SwitchQualityAsync(prepared, TestContext.Current.CancellationToken);
        Assert.False(old.IsReady);
        Assert.Equal(prepared.ModelHash, fixture.Bootstrap.RuntimeManager!.ModelHash);
        Assert.Equal(QualityPreset.Q4Base06B, fixture.Configuration.Quality);
        fixture.Bootstrap.CommitQuality(prepared.Quality);
        Assert.Equal(QualityPreset.Q8Base17B, fixture.Configuration.Quality);
        Assert.Equal(ComputePreference.Manual, fixture.Configuration.Compute);
        Assert.Equal("cpu", fixture.Configuration.DesiredBackendName);
        Assert.Equal(1, fixture.PeakModels);
        Assert.Equal(1, fixture.LiveModels);
        Assert.Equal(2, fixture.Published.Count);
    }

    [Fact]
    public async Task RetirementCompletesBeforeReplacementConstruction()
    {
        await using var fixture = new Fixture();
        await fixture.Activate(QualityPreset.Q4Base06B);
        var target = await fixture.Bootstrap.PrepareQualityAsync(QualityPreset.Q8Base06B, TestContext.Current.CancellationToken);
        fixture.BlockRetirement = true;
        var change = fixture.Bootstrap.SwitchQualityAsync(target, TestContext.Current.CancellationToken);
        try
        {
            var first = await Task.WhenAny(fixture.RetirementEntered.Task, change).WaitAsync(TestContext.Current.CancellationToken);
            Assert.Same(fixture.RetirementEntered.Task, first);
            Assert.Single(fixture.Requests);
            Assert.Equal(1, fixture.LiveModels);
        }
        finally { fixture.ReleaseRetirement.TrySetResult(); }
        await change;
        Assert.Equal(1, fixture.PeakModels);
    }

    [Fact]
    public async Task MissingDownloadDoesNotRetireCurrentRuntime()
    {
        await using var fixture = new Fixture();
        var old = await fixture.Activate(QualityPreset.Q4Base06B);
        File.Delete(Path.Combine(fixture.Models, "base-q8.gguf"));
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Bootstrap.PrepareQualityAsync(
            QualityPreset.Q8Base06B, TestContext.Current.CancellationToken));
        Assert.Same(old, fixture.Bootstrap.RuntimeManager);
        Assert.True(fixture.Bootstrap.IsReady);
        Assert.Equal(1, fixture.LiveModels);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task FailedInitializationRestoresPreviousRuntimeAndKeepsSavedQuality()
    {
        await using var fixture = new Fixture();
        var old = await fixture.Activate(QualityPreset.Q4Base06B);
        var target = await fixture.Bootstrap.PrepareQualityAsync(QualityPreset.Q8Base06B, TestContext.Current.CancellationToken);
        fixture.Failures.Enqueue(QualityPreset.Q8Base06B);
        await Assert.ThrowsAsync<IOException>(() => fixture.Bootstrap.SwitchQualityAsync(target, TestContext.Current.CancellationToken));
        Assert.NotSame(old, fixture.Bootstrap.RuntimeManager);
        Assert.Equal(old.ModelHash, fixture.Bootstrap.RuntimeManager!.ModelHash);
        Assert.Equal(QualityPreset.Q4Base06B, fixture.Configuration.Quality);
        Assert.Equal(BootstrapState.Ready, fixture.Bootstrap.State);
        Assert.True(fixture.Bootstrap.IsReady);
        Assert.Equal(1, fixture.LiveModels);
        Assert.Equal(1, fixture.PeakModels);
        Assert.Equal(new[] { QualityPreset.Q4Base06B, QualityPreset.Q8Base06B, QualityPreset.Q4Base06B }, fixture.Requests);
    }

    [Fact]
    public async Task FailedRollbackReportsFailureAndCanRetryAfterCleanNativeRetirement()
    {
        await using var fixture = new Fixture();
        await fixture.Activate(QualityPreset.Q4Base06B);
        var target = await fixture.Bootstrap.PrepareQualityAsync(QualityPreset.Q8Base06B, TestContext.Current.CancellationToken);
        fixture.Failures.Enqueue(QualityPreset.Q8Base06B);
        fixture.Failures.Enqueue(QualityPreset.Q4Base06B);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => fixture.Bootstrap.SwitchQualityAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal(BootstrapState.Failed, fixture.Bootstrap.State);
        Assert.False(fixture.Bootstrap.IsReady);
        Assert.Equal(0, fixture.LiveModels);
        Assert.False(fixture.Lease.IsPoisoned);
        await fixture.Activate(QualityPreset.Q4Base17B);
        Assert.True(fixture.Bootstrap.IsReady);
        Assert.Equal(1, fixture.LiveModels);
        Assert.Null(fixture.Bootstrap.Failure);
    }

    [Fact]
    public async Task UncertainRetirementNeverStartsAnotherModel()
    {
        await using var fixture = new Fixture();
        await fixture.Activate(QualityPreset.Q4Base06B);
        var target = await fixture.Bootstrap.PrepareQualityAsync(QualityPreset.Q8Base06B, TestContext.Current.CancellationToken);
        fixture.ExpectedCleanupFailure = true;
        fixture.FailRetirement = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Bootstrap.SwitchQualityAsync(target, TestContext.Current.CancellationToken));
        Assert.True(fixture.Lease.IsPoisoned);
        Assert.Single(fixture.Requests);
        Assert.Equal(BootstrapState.Failed, fixture.Bootstrap.State);
        Assert.False(fixture.Bootstrap.IsReady);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDuringInitializationDoesNotPublishOrRestoreModels(bool ignoresCancellation)
    {
        await using var fixture = new Fixture();
        await fixture.Activate(QualityPreset.Q4Base06B);
        var target = await fixture.Bootstrap.PrepareQualityAsync(QualityPreset.Q8Base06B, TestContext.Current.CancellationToken);
        fixture.BlockInitialization = true;
        fixture.IgnoreInitializationCancellation = ignoresCancellation;
        var change = fixture.Bootstrap.SwitchQualityAsync(target, TestContext.Current.CancellationToken);
        await fixture.InitializationEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        fixture.Bootstrap.CancelPendingOperations();
        if (ignoresCancellation)
        {
            Assert.False(change.IsCompleted);
            fixture.ReleaseInitialization.TrySetResult();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);
        Assert.Single(fixture.Published);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal(0, fixture.LiveModels);
        Assert.Equal(BootstrapState.Stopped, fixture.Bootstrap.State);
    }

    [Fact]
    public async Task SaveFailureRestoresConfigurationBeforeCallerRollsBackRuntime()
    {
        await using var fixture = new Fixture();
        await fixture.Activate(QualityPreset.Q4Base06B);
        var previous = fixture.Bootstrap.ActiveModels!;
        var benchmark = fixture.Configuration.BackendBenchmark = new("fixture", "cpu", DateTimeOffset.UtcNow, []);
        var target = await fixture.Bootstrap.PrepareQualityAsync(QualityPreset.Q8Base06B, TestContext.Current.CancellationToken);
        await fixture.Bootstrap.SwitchQualityAsync(target, TestContext.Current.CancellationToken);
        fixture.FailSave = true;
        Assert.Throws<IOException>(() => fixture.Bootstrap.CommitQuality(target.Quality));
        Assert.Equal(QualityPreset.Q4Base06B, fixture.Configuration.Quality);
        Assert.Same(benchmark, fixture.Configuration.BackendBenchmark);
        await fixture.Bootstrap.SwitchQualityAsync(previous, TestContext.Current.CancellationToken);
        Assert.Equal(previous.ModelHash, fixture.Bootstrap.RuntimeManager!.ModelHash);
        Assert.Equal(1, fixture.PeakModels);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(AppContext.BaseDirectory, "quality-switch-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient http = new(new MissingAssetHandler());
        public readonly Configuration Configuration = new() { KeepBaseModelLoaded = false };
        public readonly LeaseBoundary Lease = new();
        public readonly Queue<QualityPreset> Failures = new();
        public readonly List<QualityPreset> Requests = [];
        public readonly List<string> Published = [];
        public readonly TaskCompletionSource InitializationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseInitialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource RetirementEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseRetirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BootstrapService Bootstrap { get; }
        public string Models => Path.Combine(root, "data", "models");
        public int LiveModels;
        public int PeakModels;
        public bool FailRetirement;
        public bool ExpectedCleanupFailure;
        public bool BlockInitialization;
        public bool IgnoreInitializationCancellation;
        public bool BlockRetirement;
        public bool FailSave;

        public Fixture()
        {
            var plugin = Path.Combine(root, "plugin");
            Directory.CreateDirectory(Path.Combine(plugin, "assets"));
            Directory.CreateDirectory(Path.Combine(plugin, "reference-extractor"));
            Directory.CreateDirectory(Models);
            var names = new[] { "base-q4", "base-q8", "base-1.7b-q4", "base-1.7b-q8", "tokenizer-q4", "tokenizer-q8" };
            var artifacts = names.Select(name =>
            {
                var content = Encoding.UTF8.GetBytes("model fixture " + name);
                File.WriteAllBytes(Path.Combine(Models, name + ".gguf"), content);
                return new AssetArtifact(name, name + ".gguf", new Uri("https://example.invalid/" + name),
                    content.Length, Convert.ToHexStringLower(SHA256.HashData(content)), QwenNative.AbiVersion, "test", 0);
            }).ToArray();
            File.WriteAllText(Path.Combine(plugin, "assets", "models.json"), JsonSerializer.Serialize(new AssetManifest(1, "fixture", artifacts)));
            var executable = OperatingSystem.IsWindows() ? "ReferenceExtractor.exe" : "ReferenceExtractor";
            foreach (var name in new[] { executable, "ReferenceExtractor.dll", "ReferenceExtractor.deps.json", "ReferenceExtractor.runtimeconfig.json" })
                File.WriteAllText(Path.Combine(plugin, "reference-extractor", name), name);
            Bootstrap = new BootstrapService(plugin, Path.Combine(root, "data"), Configuration, () =>
            {
                if (FailSave) throw new IOException("save failed");
            }, Lease, Initialize, http);
            Bootstrap.Ready += runtime => Published.Add(runtime.ModelHash);
        }

        public async Task<RuntimeManager> Activate(QualityPreset quality)
        {
            var prepared = await Bootstrap.PrepareQualityAsync(quality, TestContext.Current.CancellationToken);
            await Bootstrap.SwitchQualityAsync(prepared, TestContext.Current.CancellationToken);
            return Bootstrap.RuntimeManager!;
        }

        private async Task<RuntimeManager> Initialize(PreparedModelQuality models, CancellationToken token)
        {
            Requests.Add(models.Quality);
            LiveModels++;
            PeakModels = Math.Max(PeakModels, LiveModels);
            var manager = new RuntimeManager(Configuration, () => { }, models.BasePath, models.CodecPath,
                models.ModelHash, "fixture", Path.Combine(root, "runtime"),
                Path.Combine(root, "helper"), Path.Combine(root, "references"), nativeFailureReporter: Lease.Poison);
            manager.SetPluginLifetimeLease(Lease);
            var backend = new BackendInfo("cpu", "CPU", BackendType.Cpu, 0, 1, 1);
            manager.SetTestBackendState(new(backend, backend, false, null));
            manager.SetTestSeam(new ResidencyBoundary(this));
            try
            {
                if (BlockInitialization)
                {
                    InitializationEntered.TrySetResult();
                    if (IgnoreInitializationCancellation) await ReleaseInitialization.Task;
                    else await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                if (Failures.TryPeek(out var failed) && failed == models.Quality)
                {
                    Failures.Dequeue();
                    throw new IOException("initialization failed");
                }
                return manager;
            }
            catch
            {
                await manager.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await Bootstrap.DisposeAsync(); }
            catch (IOException error) when (ExpectedCleanupFailure && error.Message == "retirement failed") { }
            finally
            {
                http.Dispose();
                TestDirectory.Delete(root);
            }
        }
    }

    private sealed class ResidencyBoundary(Fixture fixture) : IRuntimeManagerTestSeam
    {
        private bool ready = true;
        public Exception? NativeOwnershipFailure => null;
        public bool BaseRuntimeReady => ready;
        public async Task WaitForBaseOwnershipAsync(string operation, Func<Task> wait, CancellationToken token)
        {
            await wait();
            if (fixture.BlockRetirement && operation == nameof(RuntimeManager.DisposeAsync))
            {
                fixture.RetirementEntered.TrySetResult();
                await fixture.ReleaseRetirement.Task;
            }
        }
        public void NativeOperationStarted(string operation) { }
        public void BaseRuntimeDisposed()
        {
            if (!ready) return;
            if (fixture.FailRetirement) throw new IOException("retirement failed");
            ready = false;
            fixture.LiveModels--;
        }
    }

    private sealed class LeaseBoundary : IProcessLifetimeLease
    {
        public bool IsPoisoned { get; private set; }
        public void Poison(Exception failure) => IsPoisoned = true;
        public void Dispose() { }
    }

    private sealed class MissingAssetHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
