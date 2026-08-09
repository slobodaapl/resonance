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
    private Task? task;
    private Task? optionalRuntimeTask;

    public BootstrapState State { get; private set; } = BootstrapState.Starting;
    public RuntimeManager? RuntimeManager { get; private set; }
    public bool IsReady => RuntimeManager is not null && State is not BootstrapState.Failed and not BootstrapState.Stopped;
    public string? VoiceDesignPath { get; private set; }
    public Exception? Failure { get; private set; }
    public bool? CudaDriverAvailable { get; private set; }
    public AssetProgress? CurrentProgress { get; private set; }
    public event Action<BootstrapState>? StateChanged;
    public event Action<AssetProgress>? Progress;
    public event Action<RuntimeManager>? Ready;
    public event Action<string, string>? VoiceDesignReady;
    public event Action<Exception>? OptionalRuntimeFailed;
    public event Action<Exception>? OptionalPreparationFailed;

    public BootstrapService(string pluginDirectory, string dataDirectory, Configuration configuration, Action saveConfiguration)
    {
        manifestPath = Path.Combine(pluginDirectory, "assets", "models.json");
        runtimeManifestPath = Path.Combine(pluginDirectory, "assets", "runtimes.json");
        assets = new AssetManager(Path.Combine(dataDirectory, "models"));
        runtimeDirectory = Path.Combine(dataDirectory, "runtimes");
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

    public void Start()
    {
        if (task is not null) return;
        task = Task.Run(() => RunAsync(shutdown.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            var manifest = await AssetManager.LoadManifestAsync(manifestPath, token).ConfigureAwait(false);
            var quality = configuration.Quality == QualityPreset.High ? "q8" : "q4";
            var baseModel = manifest.Artifacts.Single(value => value.Id == $"base-{quality}");
            var tokenizer = manifest.Artifacts.Single(value => value.Id == $"tokenizer-{quality}");
            var design = manifest.Artifacts.Single(value => value.Id == $"voicedesign-{quality}");
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
                baseModel.Sha256, design.Sha256, runtimeVersion, runtimeDirectory);
            await manager.InitializeAsync(token).ConfigureAwait(false);
            RuntimeManager = manager;
            SetState(BootstrapState.Ready);
            Ready?.Invoke(manager);
            optionalRuntimeTask = EnsureOptionalRuntimesAsync(manager, token);
            try
            {
                SetState(BootstrapState.DownloadingVoiceDesign);
                VoiceDesignPath = await assets.EnsureAsync(design, token).ConfigureAwait(false);
                if (optionalRuntimeTask is not null) await optionalRuntimeTask.ConfigureAwait(false);
                SetState(BootstrapState.BenchmarkingBackends);
                await manager.BenchmarkAndApplyAsync(VoiceDesignPath, token).ConfigureAwait(false);
                VoiceDesignReady?.Invoke(VoiceDesignPath, codecPath);
                SetState(BootstrapState.Ready);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
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
            CudaDriverAvailable = CudaDriverProbe.IsAvailable();
            await runtimePacks.EnsureMatchingAsync(runtimeManifestPath, manager.DetectedBackends, token,
                CudaDriverAvailable.Value).ConfigureAwait(false);
            await manager.RefreshBackendsAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            CudaDriverAvailable ??= false;
            OptionalRuntimeFailed?.Invoke(error);
        }
    }

    private void SetState(BootstrapState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        if (task is not null) await task.ConfigureAwait(false);
        if (optionalRuntimeTask is not null) await optionalRuntimeTask.ConfigureAwait(false);
        if (RuntimeManager is not null) await RuntimeManager.DisposeAsync().ConfigureAwait(false);
        assets.Dispose();
        runtimePacks.Dispose();
        shutdown.Dispose();
    }
}
