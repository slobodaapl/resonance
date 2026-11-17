using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Resonance.Audio;
using Resonance.Bootstrap;
using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.UI;

public sealed partial class ConfigWindow : Window, IAsyncDisposable
{
    private static readonly string[] Languages = ["english", "japanese", "german", "french"];
    private static readonly string[] Sexes = ["masculine", "feminine"];
    private const string SampleSentence = "The light of the crystal guides us through the dark. <sigh> Let's keep moving.";

    private readonly Configuration configuration;
    private readonly Func<RuntimeManager?> runtime;
    private readonly Action save;
    private readonly Action<Exception> reportError;
    private readonly Func<BootstrapService> bootstrap;
    private readonly Func<uint> currentTerritory;
    private readonly Func<string?> currentTerritoryName;
    private readonly Func<string> currentLanguage;
    private readonly CastingProfileCatalog catalog;
    private readonly Func<CastingPoolSnapshot?> poolSnapshot;
    private readonly Func<CancellationToken, Task> regenerateCurrentTerritory;
    private readonly Func<string, CancellationToken, Task> regenerateDomain;
    private readonly Func<string, DebugInferenceSnapshot> debugSnapshot;
    private readonly Func<string, CancellationToken, Task> refreshDebugBaseVoices;
    private readonly Func<string, string, string, CancellationToken, Task> runBaseDebug;
    private readonly Func<AudioBackendStatus> audioBackendStatus;
    private readonly Func<BackendInfo, CancellationToken, Task>? setBackend;
    private readonly Func<CancellationToken, Task>? rebuildBackendBenchmark;
    private readonly Action cancelDebug;
    private readonly Func<CancellationToken, Task>? applyAutomaticBackend;
    private readonly Action preDubConfigurationChanged;
    private readonly Action dialogueSoundEffectsChanged;
    private readonly QualityPreset loadedQuality;
    private readonly Func<PlaybackActivity> playbackActivity;
    private readonly Func<CancellationToken, Task> refreshDeviceMemory;
    private readonly Func<QualityPreset, CancellationToken, Task> setQuality;
    private readonly IFontHandle headingFont;
    private PlaybackActivity frameActivity;
    private string? operationError;
    private string selectedDomain = "generic_world";
    private string debugLanguage = "english";
    private int benchmarkInFlight;
    private readonly object taskGate = new();
    private readonly HashSet<Task> activeTasks = [];
    private readonly CancellationTokenSource shutdown = new();
    private int disposed;
    private Task? disposeTask;

    public ConfigWindow(
        Configuration configuration,
        Func<RuntimeManager?> runtime,
        Func<BootstrapService> bootstrap,
        Func<uint> currentTerritory,
        Func<string?> currentTerritoryName,
        Func<string> currentLanguage,
        CastingProfileCatalog catalog,
        Func<CastingPoolSnapshot?> poolSnapshot,
        Func<CancellationToken, Task> regenerateCurrentTerritory,
        Func<string, CancellationToken, Task> regenerateDomain,
        Func<string, DebugInferenceSnapshot> debugSnapshot,
        Func<string, CancellationToken, Task> refreshDebugBaseVoices,
        Func<string, string, string, CancellationToken, Task> runBaseDebug,
        Action cancelDebug,
        Action preDubConfigurationChanged,
        Action save,
        Action<Exception> reportError,
        Func<PlaybackActivity> playbackActivity,
        Func<CancellationToken, Task> refreshDeviceMemory,
        Func<QualityPreset, CancellationToken, Task> setQuality,
        IUiBuilder uiBuilder,
        Func<AudioBackendStatus>? audioBackendStatus = null,
        Func<BackendInfo, CancellationToken, Task>? setBackend = null,
        Func<CancellationToken, Task>? rebuildBackendBenchmark = null,
        Func<CancellationToken, Task>? applyAutomaticBackend = null,
        Action? dialogueSoundEffectsChanged = null)
        : base("Voice settings###ResonanceSettings")
    {
        this.configuration = configuration;
        loadedQuality = configuration.Quality;
        this.runtime = runtime;
        this.bootstrap = bootstrap;
        this.currentTerritory = currentTerritory;
        this.currentTerritoryName = currentTerritoryName;
        this.currentLanguage = currentLanguage;
        this.catalog = catalog;
        this.poolSnapshot = poolSnapshot;
        this.regenerateCurrentTerritory = regenerateCurrentTerritory;
        this.regenerateDomain = regenerateDomain;
        this.debugSnapshot = debugSnapshot;
        this.refreshDebugBaseVoices = refreshDebugBaseVoices;
        this.runBaseDebug = runBaseDebug;
        this.audioBackendStatus = audioBackendStatus ?? (() => new AudioBackendStatus(
            AudioOutputBackend.FfxivGameMixer, false, false, false, false,
            "FFXIV game mixer backend is not installed"));
        this.setBackend = setBackend;
        this.rebuildBackendBenchmark = rebuildBackendBenchmark;
        this.applyAutomaticBackend = applyAutomaticBackend;
        this.playbackActivity = playbackActivity;
        this.refreshDeviceMemory = refreshDeviceMemory;
        this.setQuality = setQuality;
        headingFont = uiBuilder.FontAtlas
            .NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, 18) { Weight = 1f });
        this.cancelDebug = cancelDebug;
        this.preDubConfigurationChanged = preDubConfigurationChanged;
        this.dialogueSoundEffectsChanged = dialogueSoundEffectsChanged ?? (() => { });
        this.save = save;
        this.reportError = reportError;
        debugLanguage = CurrentLanguageName();
        voiceLanguage = debugLanguage;
        Size = new Vector2(1040, 800);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 560),
            MaximumSize = new Vector2(float.MaxValue),
        };
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private string CurrentLanguageName()
    {
        var value = currentLanguage().Trim().ToLowerInvariant();
        return Languages.Contains(value, StringComparer.Ordinal) ? value : "english";
    }

    private async Task RebuildBackendBenchmarkAsync(
        RuntimeManager manager, CancellationToken token)
    {
        if (Interlocked.Exchange(ref benchmarkInFlight, 1) != 0) return;
        try
        {
            configuration.BackendBenchmark = null;
            save();
            if (rebuildBackendBenchmark is not null)
                await rebuildBackendBenchmark(token).ConfigureAwait(false);
            else
                await manager.BenchmarkAndApplyAsync(token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref benchmarkInFlight, 0);
        }
    }

    private void Run(Func<Task> operation)
    {
        lock (taskGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            Task task;
            Volatile.Write(ref operationError, null);
            try { task = operation(); }
            catch (Exception error)
            {
                Volatile.Write(ref operationError, error.Message);
                reportError(error);
                return;
            }
            activeTasks.Add(task);
            _ = ObserveTaskAsync(task);
        }
    }

    private async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Volatile.Write(ref operationError, error.Message);
            reportError(error);
        }
        finally
        {
            lock (taskGate) activeTasks.Remove(task);
        }
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (taskGate)
        {
            if (disposeTask is not null) return new ValueTask(disposeTask);
            Volatile.Write(ref disposed, 1);
            disposeTask = DisposeCoreAsync(activeTasks.ToArray(), start.Task);
            task = disposeTask;
        }

        // Do not invoke cancellation callbacks while taskGate is held.  Some
        // callbacks remove their task from that gate or re-enter disposal.
        try { shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        finally { start.TrySetResult(); }
        return new ValueTask(task);
    }

    private async Task DisposeCoreAsync(Task[] tasks, Task start)
    {
        await start.ConfigureAwait(false);
        foreach (var task in tasks)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            // ObserveTaskAsync owns reporting. Waiting here only drains the
            // task before dependent runtime/database disposal; reporting a
            // second time would duplicate the same UI failure.
            catch (Exception) { }
        }
        shutdown.Dispose();
        headingFont.Dispose();
    }
}
