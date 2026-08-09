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
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static INotificationManager Notifications { get; private set; } = null!;

    private readonly WindowSystem windows = new("Resonance");
    private readonly ConfigWindow configWindow;
    private readonly Database database;
    private readonly CutsceneDetector cutscenes;
    private readonly TalkObserver talk;
    private readonly NativeVoiceDetector nativeVoice;
    private readonly BootstrapService bootstrap;
    private readonly SessionCoordinator coordinator;
    private readonly IpcService ipc;
    private readonly CastingProfileCatalog catalog;
    private string? notifiedFallbackKey;
    private int disposed;

    public Configuration Configuration { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var dataDirectory = PluginInterface.ConfigDirectory.FullName;
        catalog = CastingProfileCatalog.Load(Path.Combine(
            PluginInterface.AssemblyLocation.Directory!.FullName, "assets", "dub-profiles.json"));
        var officialVoiceCatalog = OfficialVoiceCatalog.Load(Path.Combine(
            PluginInterface.AssemblyLocation.Directory!.FullName, "assets", "official-voices.json"));
        Configuration.MigrateCastingV2(catalog, EnglishTerritoryName,
            message => Log.Information("Casting configuration: {Message}", message));
        PluginInterface.SavePluginConfig(Configuration);
        database = new Database(Path.Combine(dataDirectory, "resonance.sqlite3"));
        cutscenes = new CutsceneDetector(Condition);
        talk = new TalkObserver(AddonLifecycle);
        nativeVoice = new NativeVoiceDetector(SigScanner, GameInterop, Log);
        bootstrap = new BootstrapService(
            PluginInterface.AssemblyLocation.Directory!.FullName,
            dataDirectory,
            Configuration,
            SaveConfiguration);
        coordinator = new SessionCoordinator(
            cutscenes, talk, ClientState, Condition, Framework,
            new SpeakerResolver(ObjectTable, DataManager, catalog,
                () => EnglishTerritoryName(ClientState.TerritoryType)),
            new QuestDialoguePrefetcher(DataManager, ClientState), nativeVoice, new LipSyncService(Framework, ObjectTable),
            database, bootstrap, new GameVolumeService(GameConfig), Path.Combine(dataDirectory, "line-cache"),
            new ScdExtractor(DataManager), Path.Combine(dataDirectory, "official-working"),
            catalog, officialVoiceCatalog, EnglishTerritoryName,
            Configuration, Log);
        ipc = new IpcService(PluginInterface, bootstrap, coordinator);

        configWindow = new ConfigWindow(Configuration, () => bootstrap.RuntimeManager, () => bootstrap,
            () => ClientState.TerritoryType,
            () => EnglishTerritoryName(ClientState.TerritoryType),
            () => ClientState.ClientLanguage.ToString().ToLowerInvariant(),
            catalog,
            coordinator.GetCastingPoolSnapshot,
            () => (nativeVoice.IsAvailable, nativeVoice.UnavailableReason),
            coordinator.RegenerateCurrentTerritoryVoicesAsync,
            coordinator.RegenerateDomainVoicesAsync,
            coordinator.GetDebugInferenceSnapshot,
            coordinator.RefreshDebugBaseVoicesAsync,
            coordinator.RunVoiceDesignDebugAsync,
            coordinator.RunBaseDebugAsync,
            coordinator.CancelDebugInference,
            SaveConfiguration, ReportError);
        windows.AddWindow(configWindow);
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;

        bootstrap.StateChanged += OnBootstrapState;
        bootstrap.Ready += OnRuntimeReady;
        bootstrap.OptionalRuntimeFailed += OnOptionalRuntimeFailed;
        bootstrap.OptionalPreparationFailed += OnOptionalPreparationFailed;
        bootstrap.Start();
        if (!nativeVoice.IsAvailable)
            Notifications.AddNotification(new Notification
            {
                Title = "Resonance disabled for safety",
                Content = $"Native voice detection is unavailable ({nativeVoice.UnavailableReason}). Synthetic cutscene playback is suppressed to avoid talking over official voices.",
                Type = NotificationType.Error,
                Minimized = false,
            });
    }

    private void OnRuntimeReady(RuntimeManager manager)
    {
        manager.SelectionChanged += OnBackendSelection;
        if (manager.Selection is not null) OnBackendSelection(manager.Selection);
    }

    private void OnBackendSelection(BackendSelection selection)
    {
        if (!selection.IsTemporaryCpuFallback || selection.Error is null)
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
        if (state == BootstrapState.Failed && bootstrap.Failure is not null) ReportError(bootstrap.Failure);
    }

    private void OnOptionalRuntimeFailed(Exception error)
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
    }

    private void OnOptionalPreparationFailed(Exception error)
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
    }

    private void ReportError(Exception error)
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
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        windows.RemoveAllWindows();
        ipc.Dispose();
        coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        bootstrap.DisposeAsync().AsTask().GetAwaiter().GetResult();
        talk.Dispose();
        nativeVoice.Dispose();
        cutscenes.Dispose();
        database.Dispose();
    }
}
