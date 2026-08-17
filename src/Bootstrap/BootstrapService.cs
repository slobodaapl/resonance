using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Bootstrap;

public enum BootstrapState { Starting, DownloadingRuntime, DownloadingBase, InitializingRuntime, Ready, DownloadingVoiceDesign, BenchmarkingBackends, Failed, Stopped }

public sealed class BootstrapService : IAsyncDisposable
{
    private readonly CancellationTokenSource shutdown = new();
    private readonly AssetManager assets;
    private readonly string manifestPath;
    private readonly Configuration configuration;
    private readonly Action saveConfiguration;
    private readonly RuntimePackManager runtimePacks;
    private readonly string runtimeManifestPath;
    private readonly string runtimeDirectory;
    private readonly string referenceExtractorPath;
    private readonly string referenceExtractionDirectory;
    private readonly object disposeGate = new();
    private readonly IProcessLifetimeLease lifetimeLease;
    private Task? task;
    private Task? optionalRuntimeTask;
    private Task? disposeTask;
    private int disposed;

    public BootstrapState State { get; private set; } = BootstrapState.Starting;
    public RuntimeManager? RuntimeManager { get; private set; }
    public bool IsReady => RuntimeManager is { IsReady: true, HasNativeOwnershipFailure: false }
                           && State is not BootstrapState.Failed and not BootstrapState.Stopped;
    public string? VoiceDesignPath { get; private set; }
    public Exception? Failure { get; private set; }
    public bool? CudaDriverAvailable { get; private set; }
    public bool IsWineRuntime { get; private set; }
    public IReadOnlyList<string> MissingProtonCudaVariables { get; private set; } = [];
    public AssetProgress? CurrentProgress { get; private set; }
    public event Action<BootstrapState>? StateChanged;
    public event Action<AssetProgress>? Progress;
    public event Action<RuntimeManager>? Ready;
    public event Action<string, string>? VoiceDesignReady;
    public event Action<Exception>? OptionalRuntimeFailed;
    public event Action<Exception>? OptionalPreparationFailed;
    public event Action? CudaDriverProbeCompleted;

    internal BootstrapService(string pluginDirectory, string dataDirectory, Configuration configuration, Action saveConfiguration,
        IProcessLifetimeLease lifetimeLease)
    {
        this.lifetimeLease = lifetimeLease ?? throw new ArgumentNullException(nameof(lifetimeLease));
        manifestPath = Path.Combine(pluginDirectory, "assets", "models.json");
        runtimeManifestPath = Path.Combine(pluginDirectory, "assets", "runtimes.json");
        assets = new AssetManager(Path.Combine(dataDirectory, "models"));
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
            var selectedAssets = ModelQualitySelection.Resolve(configuration.Quality);
            var baseModel = manifest.Artifacts.Single(value => value.Id == selectedAssets.Base);
            var tokenizer = manifest.Artifacts.Single(value => value.Id == selectedAssets.Tokenizer);
            var design = manifest.Artifacts.Single(value => value.Id == selectedAssets.VoiceDesign);
            if (baseModel.Abi != QwenNative.AbiVersion || tokenizer.Abi != QwenNative.AbiVersion
                || design.Abi != QwenNative.AbiVersion)
                throw new InvalidDataException("Selected model assets do not match the native runtime ABI");
            SetState(BootstrapState.DownloadingRuntime);
            var coreDeclared = await runtimePacks.TryEnsureCoreAsync(runtimeManifestPath, token).ConfigureAwait(false);
            if (!coreDeclared && !File.Exists(Path.Combine(runtimeDirectory, "qwen.dll")))
            {
                throw new InvalidDataException("No core runtime pack is declared and no development runtime is installed");
            }
            QwenCppRuntime.ConfigureNativeRuntimeDirectory(runtimeDirectory);
            SetState(BootstrapState.DownloadingBase);
            var basePath = await assets.EnsureAsync(baseModel, token).ConfigureAwait(false);
            var codecPath = await assets.EnsureAsync(tokenizer, token).ConfigureAwait(false);

            SetState(BootstrapState.InitializingRuntime);
            var runtimeVersion = typeof(BootstrapService).Assembly.GetName().Version?.ToString() ?? "0";
            var manager = new RuntimeManager(configuration, saveConfiguration, basePath, codecPath,
                baseModel.Sha256, design.Sha256, runtimeVersion, runtimeDirectory,
                referenceExtractorPath, referenceExtractionDirectory,
                nativeFailureReporter: lifetimeLease.Poison);
            manager.SetPluginLifetimeLease(lifetimeLease);
            RuntimeManager = manager;
            try
            {
                await manager.InitializeAsync(token).ConfigureAwait(false);
            }
            catch (Exception initializationError)
            {
                try
                {
                    await manager.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    lifetimeLease.Poison(new AggregateException(
                        "Runtime initialization and cleanup both failed", initializationError, cleanupError));
                    throw new AggregateException("Runtime initialization and cleanup both failed",
                        initializationError, cleanupError);
                }
                throw;
            }
            SetState(BootstrapState.Ready);
            Ready?.Invoke(manager);
            optionalRuntimeTask = EnsureOptionalRuntimesAsync(manager, token);
            try
            {
                SetState(BootstrapState.DownloadingVoiceDesign);
                VoiceDesignPath = await assets.EnsureAsync(design, token).ConfigureAwait(false);
                if (optionalRuntimeTask is not null) await optionalRuntimeTask.ConfigureAwait(false);
                VoiceDesignReady?.Invoke(VoiceDesignPath, codecPath);
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

    private async Task EnsureOptionalRuntimesAsync(RuntimeManager manager, CancellationToken token)
    {
        try
        {
            IsWineRuntime = RuntimeEnvironmentIdentity.IsWine();
            MissingProtonCudaVariables = RuntimeEnvironmentIdentity.MissingProtonCudaVariables(
                IsWineRuntime, Environment.GetEnvironmentVariable);
            CudaDriverAvailable = CudaDriverProbe.IsAvailable();
            CudaDriverProbeCompleted?.Invoke();
            await runtimePacks.EnsureMatchingAsync(runtimeManifestPath, manager.DetectedBackends, token,
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
