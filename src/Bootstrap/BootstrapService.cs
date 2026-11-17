using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Bootstrap;

public enum BootstrapState { Starting, DownloadingRuntime, DownloadingBase, InitializingRuntime, Ready, BenchmarkingBackends, Failed, Stopped }

public sealed class BootstrapService : IAsyncDisposable
{
    private readonly CancellationTokenSource shutdown = new();
    private readonly AssetManager assets;
    private readonly string manifestPath;
    private readonly Configuration configuration;
    private readonly Action saveConfiguration;
    private readonly RuntimePackManager runtimePacks;
    private readonly string runtimeManifestPath;
    private string runtimeDirectory;
    private readonly string referenceExtractorPath;
    private readonly string referenceExtractionDirectory;
    private readonly object disposeGate = new();
    private readonly IProcessLifetimeLease lifetimeLease;
    private readonly Func<PreparedModelQuality, CancellationToken, Task<RuntimeManager>> runtimeFactory;
    private Task? task;
    private Task? optionalRuntimeTask;
    private Task? disposeTask;
    private int disposed;
    private int changingQuality;

    public BootstrapState State { get; private set; } = BootstrapState.Starting;
    public RuntimeManager? RuntimeManager { get; private set; }
    public bool IsReady => !IsChangingQuality && RuntimeManager is { IsReady: true, HasNativeOwnershipFailure: false }
                           && State is BootstrapState.Ready;
    public bool IsChangingQuality => Volatile.Read(ref changingQuality) != 0;
    internal bool IsStopping => Volatile.Read(ref disposed) != 0 || shutdown.IsCancellationRequested;
    internal bool CanRestoreQuality => !IsStopping && !lifetimeLease.IsPoisoned
        && RuntimeManager?.HasNativeOwnershipFailure != true;
    internal PreparedModelQuality? ActiveModels { get; private set; }
    public Exception? Failure { get; private set; }
    public bool? CudaDriverAvailable { get; private set; }
    public bool IsWineRuntime { get; private set; }
    public IReadOnlyList<string> MissingProtonCudaVariables { get; private set; } = [];
    public AssetProgress? CurrentProgress { get; private set; }
    public IReadOnlyList<AssetArtifact> ModelAssets { get; private set; } = [];
    public event Action<BootstrapState>? StateChanged;
    public event Action<AssetProgress>? Progress;
    public event Action<RuntimeManager>? Ready;
    public event Action<Exception>? OptionalRuntimeFailed;
    public event Action<Exception>? OptionalPreparationFailed;
    public event Action? CudaDriverProbeCompleted;

    internal BootstrapService(string pluginDirectory, string dataDirectory, Configuration configuration, Action saveConfiguration,
        IProcessLifetimeLease lifetimeLease,
        Func<PreparedModelQuality, CancellationToken, Task<RuntimeManager>>? runtimeFactory = null,
        HttpClient? modelHttpClient = null)
    {
        this.lifetimeLease = lifetimeLease ?? throw new ArgumentNullException(nameof(lifetimeLease));
        manifestPath = Path.Combine(pluginDirectory, "assets", "models.json");
        runtimeManifestPath = Path.Combine(pluginDirectory, "assets", "runtimes.json");
        assets = new AssetManager(Path.Combine(dataDirectory, "models"), modelHttpClient);
        runtimeDirectory = Path.Combine(dataDirectory, "runtimes");
        referenceExtractorPath = StageReferenceExtractor(pluginDirectory, dataDirectory);
        referenceExtractionDirectory = Path.Combine(dataDirectory, "reference-extraction");
        runtimePacks = new RuntimePackManager(runtimeDirectory, Path.Combine(dataDirectory, "runtime-downloads"));
        void ReportProgress(AssetProgress value)
        {
            CurrentProgress = value;
            Progress?.Invoke(value);
        }
        assets.Progress += ReportProgress;
        runtimePacks.Progress += ReportProgress;
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;
        this.runtimeFactory = runtimeFactory ?? CreateRuntimeAsync;
    }

    private static string StageReferenceExtractor(string pluginDirectory, string dataDirectory)
    {
        var sourceDirectory = Path.Combine(pluginDirectory, "reference-extractor");
        var executableName = OperatingSystem.IsWindows() ? "ReferenceExtractor.exe" : "ReferenceExtractor";
        var requiredNames = new[]
        {
            executableName,
            "ReferenceExtractor.dll",
            "ReferenceExtractor.deps.json",
            "ReferenceExtractor.runtimeconfig.json",
        };
        var sourceFiles = requiredNames.Select(name => Path.Combine(sourceDirectory, name)).ToArray();
        var missing = sourceFiles.FirstOrDefault(path => !File.Exists(path));
        if (missing is not null)
            throw new FileNotFoundException("Reference extractor package is incomplete", missing);

        var identity = HelperPackageIdentity(sourceFiles);
        var destinationDirectory = Path.Combine(dataDirectory, "helper-packages", identity);
        Directory.CreateDirectory(destinationDirectory);
        var destinationFiles = requiredNames.Select(name => Path.Combine(destinationDirectory, name)).ToArray();
        if (destinationFiles.All(File.Exists))
        {
            if (HelperPackageIdentity(destinationFiles) != identity)
                throw new InvalidDataException("Staged reference extractor package was modified");
            return Path.Combine(destinationDirectory, executableName);
        }
        for (var index = 0; index < sourceFiles.Length; index++)
        {
            var source = sourceFiles[index];
            var destination = destinationFiles[index];
            var temporary = destination + ".part";
            File.Copy(source, temporary, true);
            File.Move(temporary, destination, true);
        }
        if (HelperPackageIdentity(destinationFiles) != identity)
            throw new InvalidDataException("Staged reference extractor package verification failed");
        return Path.Combine(destinationDirectory, executableName);
    }

    private static string HelperPackageIdentity(IEnumerable<string> paths)
    {
        using var packageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths)
        {
            packageHash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
            using var input = File.OpenRead(path);
            var buffer = new byte[128 * 1024];
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                packageHash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexStringLower(packageHash.GetHashAndReset());
    }

    public void Start()
    {
        if (task is not null || Volatile.Read(ref disposed) != 0) return;
        task = Task.Run(() => RunAsync(shutdown.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            var manifest = await AssetManager.LoadManifestAsync(manifestPath, token).ConfigureAwait(false);
            ModelAssets = manifest.Artifacts;
            var quality = configuration.Quality;
            var selectedAssets = ModelQualitySelection.Resolve(quality);
            var baseModel = manifest.Artifacts.Single(value => value.Id == selectedAssets.Base);
            var tokenizer = manifest.Artifacts.Single(value => value.Id == selectedAssets.Tokenizer);
            if (baseModel.Abi != QwenNative.AbiVersion || tokenizer.Abi != QwenNative.AbiVersion)
                throw new InvalidDataException("Selected model assets do not match the native runtime ABI");
            SetState(BootstrapState.DownloadingRuntime);
            var runtimeManifest = await RuntimePackManager.LoadManifestAsync(runtimeManifestPath, token).ConfigureAwait(false);
            var installedRuntime = await runtimePacks.TryEnsureCoreAsync(runtimeManifest, token).ConfigureAwait(false);
            if (installedRuntime is not null)
            {
                // The Windows loader can resolve DLL imports against a previously loaded pack.
                WindowsNativeLibrary.RequireUnloaded(["qwen.dll", "ggml.dll", "ggml-base.dll"]);
                runtimeDirectory = installedRuntime;
            }
            else if (!File.Exists(Path.Combine(runtimeDirectory, "qwen.dll")))
            {
                throw new InvalidDataException("No core runtime pack is declared and no development runtime is installed");
            }
            QwenCppRuntime.ConfigureNativeRuntimeDirectory(runtimeDirectory);
            SetState(BootstrapState.DownloadingBase);
            var basePath = await assets.EnsureAsync(baseModel, token).ConfigureAwait(false);
            var codecPath = await assets.EnsureAsync(tokenizer, token).ConfigureAwait(false);

            SetState(BootstrapState.InitializingRuntime);
            ActiveModels = new(quality, basePath, codecPath, baseModel.Sha256);
            var manager = await runtimeFactory(ActiveModels, token).ConfigureAwait(false);
            RuntimeManager = manager;
            SetState(BootstrapState.Ready);
            Ready?.Invoke(manager);
            optionalRuntimeTask = EnsureOptionalRuntimesAsync(manager, runtimeManifest, token);
            try
            {
                if (optionalRuntimeTask is not null) await optionalRuntimeTask.ConfigureAwait(false);
                SetState(BootstrapState.Ready);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (BackendBenchmarkCleanupException error)
            {
                lifetimeLease.Poison(error);
                throw;
            }
            catch (Exception error) when (manager.HasNativeOwnershipFailure)
            {
                throw new InvalidOperationException(
                    "Native inference ownership is poisoned; restart is required", error);
            }
            catch (Exception error)
            {
                await optionalRuntimeTask.ConfigureAwait(false);
                OptionalPreparationFailed?.Invoke(error);
                SetState(BootstrapState.Ready);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { SetState(BootstrapState.Stopped); }
        catch (Exception error)
        {
            Failure = error;
            SetState(BootstrapState.Failed);
        }
    }

    internal void SetQualityChanging(bool value) => Volatile.Write(ref changingQuality, value ? 1 : 0);

    internal void CancelPendingOperations() => shutdown.Cancel();

    internal void CommitQuality(QualityPreset quality)
    {
        var previous = configuration.Quality;
        var benchmark = configuration.BackendBenchmark;
        configuration.Quality = quality;
        configuration.BackendBenchmark = null;
        try { saveConfiguration(); }
        catch
        {
            configuration.Quality = previous;
            configuration.BackendBenchmark = benchmark;
            throw;
        }
    }

    internal void FailQualityChange(Exception failure)
    {
        Failure = failure;
        SetState(IsStopping ? BootstrapState.Stopped : BootstrapState.Failed);
    }

    internal async Task<PreparedModelQuality> PrepareQualityAsync(QualityPreset quality, CancellationToken token)
    {
        if (task is { } startup) await startup.WaitAsync(token).ConfigureAwait(false);
        ThrowIfSwitchUnavailable(token);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        if (ModelAssets.Count == 0)
            ModelAssets = (await AssetManager.LoadManifestAsync(manifestPath, linked.Token).ConfigureAwait(false)).Artifacts;
        var ids = ModelQualitySelection.Resolve(quality);
        var model = ModelAssets.Single(value => value.Id == ids.Base);
        var codec = ModelAssets.Single(value => value.Id == ids.Tokenizer);
        if (new[] { model, codec }.Any(value => value.Abi != QwenNative.AbiVersion))
            throw new InvalidDataException("Selected model assets do not match the native runtime ABI");
        var previousState = State;
        try
        {
            SetState(BootstrapState.DownloadingBase);
            var basePath = await assets.EnsureAsync(model, linked.Token).ConfigureAwait(false);
            var codecPath = await assets.EnsureAsync(codec, linked.Token).ConfigureAwait(false);
            return new(quality, basePath, codecPath, model.Sha256);
        }
        finally { SetState(IsStopping ? BootstrapState.Stopped : previousState); }
    }

    internal async Task SwitchQualityAsync(PreparedModelQuality models, CancellationToken token)
    {
        ThrowIfSwitchUnavailable(token);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        var previous = ActiveModels;
        SetState(BootstrapState.InitializingRuntime);
        try
        {
            await RetireRuntimeAsync().ConfigureAwait(false);
            ThrowIfSwitchUnavailable(linked.Token);
            var manager = await runtimeFactory(models, linked.Token).ConfigureAwait(false);
            RuntimeManager = manager;
            ThrowIfSwitchUnavailable(linked.Token);
            PublishRuntime(models, manager);
        }
        catch (Exception failure)
        {
            if (IsStopping || lifetimeLease.IsPoisoned || previous is null)
            {
                if (IsStopping && !lifetimeLease.IsPoisoned)
                    await RetireRuntimeAsync().ConfigureAwait(false);
                FailQualityChange(failure);
                throw;
            }
            try
            {
                await RetireRuntimeAsync().ConfigureAwait(false);
                ThrowIfSwitchUnavailable(shutdown.Token);
                var restored = await runtimeFactory(previous, shutdown.Token).ConfigureAwait(false);
                RuntimeManager = restored;
                ThrowIfSwitchUnavailable(shutdown.Token);
                PublishRuntime(previous, restored);
            }
            catch (Exception rollbackFailure)
            {
                var aggregate = new AggregateException("Model switch and restoring the previous quality failed", failure, rollbackFailure);
                FailQualityChange(aggregate);
                throw aggregate;
            }
            throw;
        }
    }

    private void ThrowIfSwitchUnavailable(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        token.ThrowIfCancellationRequested();
        if (IsStopping) throw new OperationCanceledException(shutdown.Token);
        if (lifetimeLease.IsPoisoned)
            throw new InvalidOperationException("Native inference ownership is poisoned; restart is required");
    }

    private async Task RetireRuntimeAsync()
    {
        if (RuntimeManager is not { } previous) return;
        try { await previous.DisposeAsync().ConfigureAwait(false); }
        catch (Exception failure)
        {
            lifetimeLease.Poison(failure);
            throw;
        }
        RuntimeManager = null;
    }

    private void PublishRuntime(PreparedModelQuality models, RuntimeManager manager)
    {
        RuntimeManager = manager;
        ActiveModels = models;
        Failure = null;
        SetState(BootstrapState.Ready);
        Ready?.Invoke(manager);
    }

    private async Task<RuntimeManager> CreateRuntimeAsync(PreparedModelQuality models, CancellationToken token)
    {
        var version = typeof(BootstrapService).Assembly.GetName().Version?.ToString() ?? "0";
        var manager = new RuntimeManager(configuration, saveConfiguration, models.BasePath, models.CodecPath,
            models.ModelHash, version, runtimeDirectory, referenceExtractorPath,
            referenceExtractionDirectory, nativeFailureReporter: lifetimeLease.Poison);
        manager.SetPluginLifetimeLease(lifetimeLease);
        RuntimeManager = manager;
        try
        {
            await manager.InitializeAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return manager;
        }
        catch (Exception failure)
        {
            try
            {
                await manager.DisposeAsync().ConfigureAwait(false);
                RuntimeManager = null;
            }
            catch (Exception cleanupFailure)
            {
                var aggregate = new AggregateException("Runtime initialization and cleanup both failed", failure, cleanupFailure);
                lifetimeLease.Poison(aggregate);
                throw aggregate;
            }
            throw;
        }
    }

    private async Task EnsureOptionalRuntimesAsync(RuntimeManager manager, RuntimePackManifest runtimeManifest,
        CancellationToken token)
    {
        try
        {
            IsWineRuntime = RuntimeEnvironmentIdentity.IsWine();
            MissingProtonCudaVariables = RuntimeEnvironmentIdentity.MissingProtonCudaVariables(
                IsWineRuntime, Environment.GetEnvironmentVariable);
            CudaDriverAvailable = CudaDriverProbe.IsAvailable();
            CudaDriverProbeCompleted?.Invoke();
            await runtimePacks.EnsureMatchingAsync(runtimeManifest, manager.DetectedBackends, token,
                CudaDriverAvailable.Value).ConfigureAwait(false);
            await manager.RefreshBackendsAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested
                                                 && !manager.HasAbandonedReferenceProcess) { }
        catch (Exception error)
        {
            CudaDriverAvailable ??= false;
            if (manager.HasNativeOwnershipFailure)
                throw new InvalidOperationException(
                    "Native inference ownership is poisoned; restart is required", error);
            OptionalRuntimeFailed?.Invoke(error);
        }
    }

    private void SetState(BootstrapState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            if (disposeTask is not null) return new ValueTask(disposeTask);
            Interlocked.Exchange(ref disposed, 1);
            disposeTask = DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        ExceptionDispatchInfo? failure = null;

        void Record(Exception error) => failure ??= ExceptionDispatchInfo.Capture(error);

        try { shutdown.Cancel(); }
        catch (Exception error) { Record(error); }

        async Task ObserveAsync(Task? pending)
        {
            if (pending is null) return;
            try { await pending.ConfigureAwait(false); }
            catch (Exception error) { Record(error); }
        }

        await ObserveAsync(task).ConfigureAwait(false);
        await ObserveAsync(optionalRuntimeTask).ConfigureAwait(false);
        try
        {
            if (RuntimeManager is not null)
                await RuntimeManager.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) { Record(error); }
        finally
        {
            // QwenCppRuntime refuses to free process-owned modules while any
            // runtime lease remains.  This is deliberately in finally so a
            // manager failure cannot skip the safety check.
            try { QwenCppRuntime.ReleaseNativeLibraries(); }
            catch (Exception error) { Record(error); }

            try { assets.Dispose(); }
            catch (Exception error) { Record(error); }
            try { runtimePacks.Dispose(); }
            catch (Exception error) { Record(error); }
            try { shutdown.Dispose(); }
            catch (Exception error) { Record(error); }
        }

        if (failure is not null)
        {
            try { lifetimeLease.Poison(failure.SourceException); }
            catch (Exception error) { Record(error); }
        }

        failure?.Throw();
    }
}
