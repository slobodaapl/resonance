using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Resonance.Audio;
using Resonance.Bootstrap;
using Resonance.Data;
using Resonance.Game;
using Resonance.UI;
using Resonance.Ipc;
using Resonance.Tts;
using Dalamud.Game;
using Dalamud.Game.Command;
using Lumina.Excel.Sheets;
using Lumina.Text;

namespace Resonance.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static INotificationManager Notifications { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;

    private readonly string dataDirectory;
    private readonly WindowSystem windows = new("Resonance");
    private ConfigWindow? configWindow;
    private Database? database;
    private CutsceneDetector? cutscenes;
    private TalkObserver? talk;
    private BootstrapService? bootstrap;
    private OfficialProfilePackManager? officialProfilePacks;
    private Task? officialProfilePackTask;
    private SessionCoordinator? coordinator;
    private IpcService? ipc;
    private LipSyncService? startupLipSync;
    private readonly CastingProfileCatalog catalog;
    private readonly OfficialVoiceCatalog officialVoiceCatalog;
    private readonly Func<string, CancellationToken, ValueTask<IProcessLifetimeLease>> acquireLifetimeLease;
    private readonly CancellationTokenSource startupShutdown = new();
    private IProcessLifetimeLease? lifetimeLease;
    private readonly Task startupTask;
    private readonly object disposeGate = new();
    private Task? disposeTask;
    private Exception? startupFailure;
    private string? notifiedFallbackKey;
    private int disposed;
    private int openSettingsRequested;

    public Configuration Configuration { get; }

    public Plugin() : this(ProcessLifetimeLeaseProvider.AcquireAsync) { }

    internal Plugin(Func<string, CancellationToken, ValueTask<IProcessLifetimeLease>> acquireLifetimeLease)
    {
        this.acquireLifetimeLease = acquireLifetimeLease;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        dataDirectory = PluginInterface.ConfigDirectory.FullName;
        catalog = CastingProfileCatalog.Load(Path.Combine(
            PluginInterface.AssemblyLocation.Directory!.FullName, "assets", "dub-profiles.json"));
        officialVoiceCatalog = OfficialVoiceCatalog.Load(Path.Combine(
            PluginInterface.AssemblyLocation.Directory!.FullName, "assets", "official-voices.json"));
        Configuration.MigrateCastingV2(catalog, EnglishTerritoryName,
            message => Log.Information("Casting configuration: {Message}", message));
        PluginInterface.SavePluginConfig(Configuration);
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        CommandManager.AddHandler("/resonance", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Resonance settings.",
        });
        startupTask = Task.Run(() => InitializeAsync(startupShutdown.Token));
    }

    private async Task InitializeAsync(CancellationToken token)
    {
        IProcessLifetimeLease? lease = null;
        try
        {
            await ProcessTeardownBarrier.WaitAsync(token).ConfigureAwait(false);
            ProcessTeardownBarrier.ThrowIfBlocked();
            var acquiredLease = await acquireLifetimeLease(dataDirectory, token).ConfigureAwait(false);
            var accepted = false;
            lock (disposeGate)
            {
                if (Volatile.Read(ref disposed) == 0 && !startupShutdown.IsCancellationRequested)
                {
                    lifetimeLease = acquiredLease;
                    accepted = true;
                }
            }
            if (!accepted)
            {
                try { acquiredLease.Dispose(); }
                catch (Exception releaseError)
                {
                    acquiredLease.Poison(releaseError);
                    Log.Error(releaseError, "Rejected Resonance native lifetime lease could not be released");
                }
                return;
            }

            lease = acquiredLease;
            var dispatch = Framework.RunOnFrameworkThread(() => InitializeServicesOnFramework(acquiredLease));
            await FrameworkDispatchObserver.AwaitAsync(dispatch, token, Log,
                "Resonance service initialization dispatch failed").ConfigureAwait(false);
            if (Volatile.Read(ref disposed) != 0)
            {
                var cleanupFailures = await DisposeOwnedServicesAsync(lease).ConfigureAwait(false);
                if (cleanupFailures.Count > 0)
                    Log.Error(new AggregateException("Resonance late-start cleanup failed", cleanupFailures),
                        "Resonance late-start cleanup failed; restart is required");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            startupFailure = error;
            Log.Error(error, "Resonance startup failed; restart required before native lifetime can be initialized");
            Interlocked.Exchange(ref disposed, 1);
            DetachPluginSubscriptions();
            try
            {
                var cleanupFailures = await DisposeOwnedServicesAsync(lease).ConfigureAwait(false);
                if (cleanupFailures.Count > 0)
                    Log.Error(new AggregateException("Resonance startup cleanup failed", cleanupFailures),
                        "Resonance startup cleanup failed; restart is required");
            }
            catch (Exception cleanupError)
            {
                Log.Error(cleanupError, "Resonance startup cleanup failed; restart is required");
            }
            QueueFrameworkUi(() => Notifications.AddNotification(new Notification
            {
                Title = "Resonance startup failed",
                Content = $"Native lifetime startup failed: {error.Message} Restart the game if the failure persists.",
                Type = NotificationType.Error,
                InitialDuration = TimeSpan.FromSeconds(20),
                Minimized = false,
            }), "Startup failure notification dispatch failed");
        }
    }

    private void InitializeServicesOnFramework(IProcessLifetimeLease lease)
    {
        lock (disposeGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            var createdDatabase = database = new Database(Path.Combine(dataDirectory, "resonance.sqlite3"));
            var createdVoices = new VoiceRegistry(createdDatabase);
            officialProfilePacks = new OfficialProfilePackManager(
                dataDirectory, createdDatabase, createdVoices, officialVoiceCatalog, catalog);
            var createdCutscenes = cutscenes = new CutsceneDetector(Condition);
            SessionCoordinator? createdCoordinator = null;
            var createdTalk = talk = new TalkObserver(
                AddonLifecycle,
                GameGui,
                () => Configuration.AutoAdvanceDiagnostics,
                () => Configuration.Enabled
                      && Configuration.AutoAdvanceDubbedCutsceneDialogue
                      && createdCoordinator?.ShouldSuppressAutomaticAdvance == true);
            var createdBootstrap = bootstrap = new BootstrapService(
                PluginInterface.AssemblyLocation.Directory!.FullName,
                dataDirectory,
                Configuration,
                SaveConfiguration,
                lease);
            var createdLipSync = startupLipSync = new LipSyncService(Framework, ObjectTable);
            var createdScdExtractor = new ScdExtractor(DataManager, Framework, Log);
            var nativeScdTemplateLoader = new NativeScdTemplateLoader(createdDatabase, createdScdExtractor);
            IGameMixerAudioBackend? createdGameMixer = null;
            try
            {
                createdGameMixer = new FfxivGameMixerAudioBackend(
                    dataDirectory,
                    new ResonanceScdResourceOverride(SigScanner, GameInterop, Log),
                    new FfxivClientSoundPlayer(Framework),
                    loadNativeScdTemplate: nativeScdTemplateLoader.LoadAsync);
            }
            catch (Exception error)
            {
                Log.Warning(error, "FFXIV native voice output unavailable");
            }
            createdCoordinator = coordinator = new SessionCoordinator(
                createdCutscenes, createdTalk, ClientState, Condition, Framework,
                new SpeakerResolver(ObjectTable, DataManager,
                    () => EnglishTerritoryName(ClientState.TerritoryType)),
                new QuestDialoguePrefetcher(DataManager, ClientState),
                new MsqProgressReader(DataManager, ClientState),
                new CutsceneVoiceManifestProvider(
                    DataManager, ClientState, Log, new CutscenePlanStore(dataDirectory)),
                createdLipSync,
                createdDatabase, createdVoices, createdBootstrap,
                Path.Combine(dataDirectory, "line-cache"),
                Path.Combine(dataDirectory, "debug-audio"),
                catalog, officialVoiceCatalog, EnglishTerritoryName,
                Configuration, Log, createdGameMixer);
            startupLipSync = null;
            ipc = new IpcService(PluginInterface, createdBootstrap, createdCoordinator);

            var createdWindow = configWindow = new ConfigWindow(Configuration, () => createdBootstrap.RuntimeManager,
                () => createdBootstrap,
                () => ClientState.TerritoryType,
                () => EnglishTerritoryName(ClientState.TerritoryType),
                () => ClientState.ClientLanguage.ToString().ToLowerInvariant(),
                catalog,
                createdCoordinator.GetCastingPoolSnapshot,
                createdCoordinator.RegenerateCurrentTerritoryVoicesAsync,
                createdCoordinator.RegenerateDomainVoicesAsync,
                createdCoordinator.GetDebugInferenceSnapshot,
                createdCoordinator.RefreshDebugBaseVoicesAsync,
                createdCoordinator.RunVoiceDesignDebugAsync,
                createdCoordinator.RunBaseDebugAsync,
                createdCoordinator.CancelDebugInference,
                createdCoordinator.NotifyPreDubConfigurationChanged,
                SaveConfiguration, ReportError,
                createdCoordinator.GetAudioBackendStatus,
                createdCoordinator.SetBackendAsync,
            createdCoordinator.RebuildBackendBenchmarkAsync);
            windows.AddWindow(createdWindow);
            if (Interlocked.Exchange(ref openSettingsRequested, 0) != 0)
                createdWindow.IsOpen = true;

            createdBootstrap.StateChanged += OnBootstrapState;
            createdBootstrap.Ready += OnRuntimeReady;
            createdBootstrap.OptionalRuntimeFailed += OnOptionalRuntimeFailed;
            createdBootstrap.OptionalPreparationFailed += OnOptionalPreparationFailed;
            createdBootstrap.CudaDriverProbeCompleted += OnCudaDriverProbeCompleted;
            createdBootstrap.Start();
        }
    }

    private void OnCommand(string command, string arguments) => OpenSettings();

    private void OpenSettings()
    {
        var window = configWindow;
        if (window is not null) window.IsOpen = true;
        else Interlocked.Exchange(ref openSettingsRequested, 1);
    }

    private void OpenConfigUi() => OpenSettings();

    private void OpenMainUi() => OpenSettings();

    private void OnRuntimeReady(RuntimeManager manager)
    {
        manager.SelectionChanged += OnBackendSelection;
        if (manager.Selection is not null) OnBackendSelection(manager.Selection);
        StartOfficialProfilePackSync(manager.ModelHash);
    }

    private void StartOfficialProfilePackSync(string modelHash)
    {
        var packs = officialProfilePacks;
        if (packs is null || Volatile.Read(ref disposed) != 0) return;
        lock (disposeGate)
        {
            if (officialProfilePackTask is { IsCompleted: false }) return;
            officialProfilePackTask = Task.Run(async () =>
            {
                try
                {
                    var result = await packs.SynchronizeAsync(modelHash, startupShutdown.Token,
                            !Configuration.DisableVoicePackAutoUpdate)
                        .ConfigureAwait(false);
                    Log.Information(
                        "Official profile pack synchronized Version={Version} Downloaded={Downloaded} Imported={Imported}",
                        result.PackVersion, result.Downloaded, result.ImportedProfiles);
                    if (result.ImportedProfiles > 0)
                        coordinator?.NotifyOfficialProfilePackImported();
                }
                catch (OperationCanceledException) when (startupShutdown.IsCancellationRequested) { }
                catch (HttpRequestException error)
                {
                    Log.Information(error,
                        "Official profile pack update check unavailable; cached/local profiles remain active");
                }
                catch (Exception error)
                {
                    Log.Warning(error,
                        "Official profile pack update failed validation; cached/local profiles remain active");
                }
            });
        }
    }

    private void OnBackendSelection(BackendSelection selection)
    {
        var backends = bootstrap?.RuntimeManager?.DetectedBackends
            .Select(value => $"{value.Name}:{value.Type}") ?? [];
        Log.Information(
            "Inference selection desired={Desired}, effective={Effective}; backends=[{Backends}]; CUDA loader={CudaLoader}; error={Error}",
            selection.Desired.Name, selection.Effective.Name, String.Join(", ", backends),
            QwenCppRuntime.CudaLoadError ?? "ok", selection.Error ?? "none");
        QueueFrameworkUi(
            () => OnBackendSelectionOnFramework(selection),
            "Backend-selection notification dispatch failed");
    }

    private void OnBackendSelectionOnFramework(BackendSelection selection)
    {
        if (!selection.IsTemporaryCpuFallback || selection.Error is null || !selection.NotifyError)
        {
            notifiedFallbackKey = null;
            return;
        }
        var key = $"{selection.Desired.Name}\0{selection.Effective.Name}\0{selection.Error}";
        if (notifiedFallbackKey == key) return;
        notifiedFallbackKey = key;
        Notifications.AddNotification(new Notification
        {
            Title = "Resonance inference device unavailable",
            Content = selection.Error,
            Type = NotificationType.Error,
            InitialDuration = TimeSpan.FromSeconds(20),
            Minimized = false,
        });
    }

    private void OnBootstrapState(BootstrapState state)
    {
        if (state != BootstrapState.Failed || bootstrap?.Failure is not { } failure) return;
        QueueFrameworkUi(() => ReportErrorOnFramework(failure),
            "Bootstrap failure notification dispatch failed");
    }

    private void OnOptionalRuntimeFailed(Exception error)
    {
        QueueFrameworkUi(() =>
        {
            Log.Warning(error, "Optional accelerated runtime installation failed");
            Notifications.AddNotification(new Notification
            {
                Title = "Resonance acceleration pack unavailable",
                Content = $"CPU/Vulkan remain available. Optional runtime download failed: {error.Message}",
                Type = NotificationType.Warning,
                InitialDuration = TimeSpan.FromSeconds(12),
                Minimized = true,
            });
        }, "Optional runtime failure notification dispatch failed");
    }

    private void OnOptionalPreparationFailed(Exception error)
    {
        QueueFrameworkUi(() =>
        {
            Log.Warning(error, "Optional VoiceDesign/backend preparation failed");
            Notifications.AddNotification(new Notification
            {
                Title = "Resonance background voice casting unavailable",
                Content = $"Base dubbing remains ready. VoiceDesign or backend benchmarking failed: {error.Message}",
                Type = NotificationType.Warning,
                InitialDuration = TimeSpan.FromSeconds(15),
                Minimized = true,
            });
        }, "Optional preparation failure notification dispatch failed");
    }

    private void OnCudaDriverProbeCompleted()
    {
        var boot = bootstrap;
        if (boot?.CudaDriverAvailable != false || !boot.IsWineRuntime) return;
        var missing = boot.MissingProtonCudaVariables;
        var content = missing.Count > 0
            ? $"Launch XIVLauncher with {String.Join(" and ", missing.Select(name => $"{name}=1"))}, then fully restart XIVLauncher and FFXIV. CPU/Vulkan remain available."
            : "The required Proton variables are present, but Proton did not install or expose nvcuda.dll in this prefix. Refresh the prefix with an UMU-visible CUDA-capable Proton build, then fully restart XIVLauncher and FFXIV. CPU/Vulkan remain available.";
        QueueFrameworkUi(() => Notifications.AddNotification(new Notification
        {
            Title = missing.Count > 0
                ? "Resonance CUDA setup required under Proton"
                : "Resonance Proton CUDA bridge unavailable",
            Content = content,
            Type = NotificationType.Warning,
            InitialDuration = TimeSpan.FromSeconds(20),
            Minimized = false,
        }), "Proton CUDA environment notification dispatch failed");
    }

    private void ReportError(Exception error) => QueueFrameworkUi(
        () => ReportErrorOnFramework(error), "Plugin error notification dispatch failed");

    private void ReportErrorOnFramework(Exception error)
    {
        Log.Error(error, "Resonance failure");
        Notifications.AddNotification(new Notification
        {
            Title = "Resonance error",
            Content = error.Message,
            Type = NotificationType.Error,
            Minimized = false,
        });
    }

    private void QueueFrameworkUi(System.Action action, string failureMessage)
    {
        if (Volatile.Read(ref disposed) != 0 || Framework.IsFrameworkUnloading) return;
        try
        {
            var dispatch = Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposed) == 0 && !Framework.IsFrameworkUnloading) action();
            });
            _ = dispatch.ContinueWith(task =>
            {
                if (task.IsFaulted) Log.Warning(task.Exception!.GetBaseException(), failureMessage);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception dispatchError) { Log.Warning(dispatchError, failureMessage); }
    }

    private void SaveConfiguration() => PluginInterface.SavePluginConfig(Configuration);

    private static string? EnglishTerritoryName(uint territoryId)
    {
        if (territoryId == 0) return null;
        try
        {
            var sheet = DataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English);
            if (!sheet.TryGetRow(territoryId, out var row) || !row.PlaceName.IsValid) return null;
            var name = row.PlaceName.Value.Name.ExtractText();
            return String.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception error)
        {
            Log.Debug(error, "Unable to resolve English TerritoryType.PlaceName for {TerritoryId}", territoryId);
            return null;
        }
    }

    public void Dispose()
    {
        Task teardown;
        lock (disposeGate)
        {
            if (disposeTask is null)
            {
                Interlocked.Exchange(ref disposed, 1);
                try { startupShutdown.Cancel(); }
                catch (ObjectDisposedException) { }
                DetachPluginSubscriptions();
                disposeTask = Task.Run(DisposeCoreAsync);
                ProcessTeardownBarrier.Publish(disposeTask);
            }
            teardown = disposeTask;
        }

        // Dalamud may invoke Dispose on the framework thread.  Waiting there
        // can deadlock teardown that is quiescing a queued framework action.
        // The task retains this Plugin and all owned services until complete;
        // later non-framework callers join the same task.
        if (!Framework.IsInFrameworkUpdateThread)
        {
            try { teardown.GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                // The process barrier retains this fault and prevents a new
                // native owner from loading.  Do not let an unload caller
                // lose that restart-required diagnostic behind a synchronous
                // Dispose exception.
                Log.Error(error, "Resonance teardown failed; restart is required before reload");
            }
        }
    }

    private async Task DisposeCoreAsync()
    {
        var failures = new List<Exception>();
        void Record(string resource, Exception error)
        {
            Log.Warning(error, "{Resource} teardown failed", resource);
            failures.Add(new InvalidOperationException($"{resource} teardown failed", error));
        }

        try { await startupTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception error) { Record("Plugin startup", error); }
        var ownedFailures = await DisposeOwnedServicesAsync(lifetimeLease).ConfigureAwait(false);
        failures.AddRange(ownedFailures);
        try { startupShutdown.Dispose(); }
        catch (Exception error) { Record("Plugin startup cancellation", error); }

        if (failures.Count > 0)
            throw new AggregateException("Resonance teardown failed", failures);
    }

    private async Task<List<Exception>> DisposeOwnedServicesAsync(IProcessLifetimeLease? expectedLease)
    {
        var failures = new List<Exception>();
        void Record(string resource, Exception error)
        {
            Log.Warning(error, "{Resource} teardown failed", resource);
            failures.Add(new InvalidOperationException($"{resource} teardown failed", error));
        }

        ConfigWindow? window;
        IpcService? ownedIpc;
        SessionCoordinator? ownedCoordinator;
        BootstrapService? ownedBootstrap;
        OfficialProfilePackManager? ownedOfficialProfilePacks;
        Task? ownedOfficialProfilePackTask;
        TalkObserver? ownedTalk;
        CutsceneDetector? ownedCutscenes;
        Database? ownedDatabase;
        LipSyncService? ownedLipSync;
        IProcessLifetimeLease? ownedLease;
        lock (disposeGate)
        {
            window = configWindow;
            configWindow = null;
            ownedIpc = ipc;
            ipc = null;
            ownedCoordinator = coordinator;
            coordinator = null;
            ownedBootstrap = bootstrap;
            bootstrap = null;
            ownedOfficialProfilePacks = officialProfilePacks;
            officialProfilePacks = null;
            ownedOfficialProfilePackTask = officialProfilePackTask;
            officialProfilePackTask = null;
            ownedTalk = talk;
            talk = null;
            ownedCutscenes = cutscenes;
            cutscenes = null;
            ownedDatabase = database;
            database = null;
            ownedLipSync = startupLipSync;
            startupLipSync = null;
            ownedLease = lifetimeLease ?? expectedLease;
            lifetimeLease = null;
        }

        try { ownedIpc?.Dispose(); }
        catch (Exception error) { Record("IPC", error); }
        if (window is not null)
        {
            try { await window.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { Record("Configuration window", error); }
        }
        if (ownedCoordinator is not null)
        {
            try { await ownedCoordinator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                Record("Session coordinator", error);
            }
        }
        if (ownedBootstrap is not null)
        {
            try { await ownedBootstrap.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                Record("Bootstrap", error);
            }
        }
        if (ownedOfficialProfilePackTask is not null)
        {
            try { await ownedOfficialProfilePackTask.ConfigureAwait(false); }
            catch (Exception error) { Record("Official profile pack synchronization", error); }
        }
        try { ownedOfficialProfilePacks?.Dispose(); }
        catch (Exception error) { Record("Official profile pack manager", error); }
        try { ownedTalk?.Dispose(); }
        catch (Exception error) { Record("Talk observer", error); }
        try { ownedCutscenes?.Dispose(); }
        catch (Exception error) { Record("Cutscene detector", error); }
        if (ownedLipSync is not null)
        {
            try { await ownedLipSync.DisposeAsync(true).ConfigureAwait(false); }
            catch (Exception error) { Record("Startup lip-sync", error); }
        }
        try { ownedDatabase?.Dispose(); }
        catch (Exception error) { Record("Database", error); }
        if (failures.Count > 0 && ownedLease is not null)
        {
            try
            {
                ownedLease.Poison(new AggregateException(
                    "Resonance native teardown failed; restart is required", failures));
            }
            catch (Exception error) { Record("Native lifetime lease poisoning", error); }
        }
        try { ownedLease?.Dispose(); }
        catch (Exception error)
        {
            Record("Native lifetime lease", error);
            try { ownedLease?.Poison(error); }
            catch (Exception poisonError) { Record("Native lifetime lease poisoning", poisonError); }
        }
        return failures;
    }

    private void DetachPluginSubscriptions()
    {
        CommandManager.RemoveHandler("/resonance");
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        if (bootstrap is not null)
        {
            bootstrap.StateChanged -= OnBootstrapState;
            bootstrap.Ready -= OnRuntimeReady;
            bootstrap.OptionalRuntimeFailed -= OnOptionalRuntimeFailed;
            bootstrap.OptionalPreparationFailed -= OnOptionalPreparationFailed;
            bootstrap.CudaDriverProbeCompleted -= OnCudaDriverProbeCompleted;
        }
        if (bootstrap?.RuntimeManager is { } manager)
            manager.SelectionChanged -= OnBackendSelection;
        windows.RemoveAllWindows();
    }
}
