using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using Resonance.Audio;
using Resonance.Bootstrap;
using Resonance.Data;
using Resonance.Game;
using Resonance.Scheduling;
using Resonance.Tts;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Resonance.Plugin;

public sealed record DebugBaseVoiceOption(string Key, string Label, bool Available, string SourceStatus = "No verified source");
public sealed record DebugInferenceSnapshot(
    bool Ready,
    bool Running,
    string Readiness,
    string Device,
    string VoiceDesignBackend,
    string Status,
    IReadOnlyList<DebugBaseVoiceOption> BaseVoices);

public sealed class SessionCoordinator : IAsyncDisposable
{
    private sealed record PendingAutoAdvance(
        long SessionEpoch,
        long LineSequence,
        long TalkSerial,
        string Speaker,
        string Text,
        IReadOnlyList<string> NextPredictionKeys);

    private sealed record TalkAdvanceContext(long TalkSerial, bool? CutsceneUnskippable);

    private sealed record FutureDialogue(
        string Key,
        string ActorToken,
        string Text,
        string? OfficialGroupId = null,
        uint? ActorNpcBaseId = null);

    private sealed record FrameworkStateSnapshot(
        string Language,
        uint TerritoryId,
        string? TerritoryPlaceName,
        bool InCutscene,
        bool InCombat,
        bool CanWork);

    private const string DebugLineSource = "Resonance inference debug";
    private const string PreDubLineSource = "Resonance pre-dub";
    private const int PreDubSceneLimit = 2;
    private const int PreDubCandidateLimit = 32;
    private static readonly TimeSpan TeardownFrameworkWait = TimeSpan.FromSeconds(5);
    private readonly CutsceneDetector cutscenes;
    private readonly TalkObserver talk;
    private readonly IClientState client;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly SpeakerResolver speakers;
    private readonly QuestDialoguePrefetcher prefetcher;
    private readonly MsqProgressReader msqProgress;
    private readonly CutsceneVoiceManifestProvider cutsceneVoices;
    private readonly LipSyncService lipSync;
    private readonly VoiceRegistry voices;
    private readonly BootstrapService bootstrap;
    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly AutoAdvanceDiagnosticGate autoAdvanceDiagnostics = new();
    private readonly LineCache lineCache;
    private readonly string debugAudioDirectory;
    private readonly CastingProfileCatalog catalog;
    private readonly OfficialVoiceCatalog officialVoiceCatalog;
    private readonly Func<uint, string?> territoryPlaceName;
    private readonly IGameMixerAudioBackend? gameMixerBackend;
    private readonly AudioBackendSessionLock audioBackendSession = new();
    private readonly SemaphoreSlim eventGate = new(1, 1);
    private readonly SemaphoreSlim debugGate = new(1, 1);
    private readonly object debugCancellationGate = new();
    private readonly object debugRefreshGate = new();
    private readonly object autoAdvanceRetryGate = new();
    private readonly object backendSwitchGate = new();
    private readonly object coordinatorTaskGate = new();
    private readonly object disposalGate = new();
    private readonly AsyncLocal<Task?> activeFrameworkDispatch = new();
    private readonly CancellationTokenSource officialObservationShutdown = new();
    private long baseHotLoadSafetyGeneration;
    private AudioEngine? audio;
    private DubScheduler? scheduler;
    private VoiceDesigner? voiceDesigner;
    private Task<VoiceDesigner>? voiceDesignerInitialization;
    private string? voiceDesignPath;
    private string? voiceDesignCodecPath;
    private RuntimeManager? runtimeManager;
    private CastingDomainPool? domainPool;
    private CutsceneSession? session;
    private CutsceneSession? preDubSession;
    private string? preDubPlanKey;
    private string? completedPreDubPlanKey;
    private int preDubLinesRemaining;
    private bool preDubFailed;
    private int suppressAutomaticAdvance;
    private TalkAdvanceContext? talkAdvanceContext;
    private long gameControlledAdvanceSerial;
    private long nextEpoch;
    private int disposed;
    private PendingAutoAdvance? pendingAutoAdvance;
    private int autoAdvanceDispatching;
    private CancellationTokenSource? autoAdvanceRetryCancellation;
    private Task? autoAdvanceRetryTask;
    private long autoAdvanceRetryGeneration;
    private long? activeAutoAdvanceRetryGeneration;
    private long talkIdleGeneration;
    private readonly ConcurrentDictionary<string, StoredVoiceProfile> profileCache = new();
    private readonly ConcurrentDictionary<long, string> speakerKeys = new();
    private readonly ConcurrentDictionary<string, string> cutsceneSpeakerKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ResolvedLineSpeaker> cutsceneSpeakerAssignments =
        new(StringComparer.Ordinal);
    private int baseHotLoadSafe;
    private int cutsceneBaseResidencyHeld;
    private IReadOnlyList<DebugBaseVoiceOption> debugBaseVoices = [];
    private readonly ConcurrentDictionary<string, StoredVoiceProfile> debugBaseProfiles = new(StringComparer.Ordinal);
    private string? debugVoiceLanguage;
    private string debugStatus = "Idle";
    private CancellationTokenSource? debugCancellation;
    private Task? debugRefreshTask;
    private string? debugRefreshLanguage;
    private long nextDebugSequence;
    private int debugRunning;
    private int debugPlaybackActive;
    private Task? debugTask;
    private DubLine? debugPlaybackLine;
    private TaskCompletionSource? debugPlaybackCompletion;
    private string? frameworkLanguage;
    private Task? backendSwitchTask;
    private Task? disposalTask;
    private int frameworkThreadId;
    private readonly HashSet<Task> coordinatorTasks = [];
    private readonly HashSet<Task> frameworkDispatchTasks = [];
    private int exclusiveOperationActive;

    public bool IsSpeaking { get; private set; }
    public event Action<DubLine>? LineStarted;
    public event Action<DubLine>? LineFinished;
    public event Action<string, string>? SpeakerProfileUpgraded;
    public string? GetSpeakerProfile(string stableKey)
    {
        var language = Volatile.Read(ref frameworkLanguage) ?? "english";
        var modelHash = Volatile.Read(ref runtimeManager)?.ModelHash;
        return modelHash is not null
               && profileCache.TryGetValue(ProfileCacheKey(stableKey, language, modelHash), out var profile)
               && String.Equals(profile.Language, language, StringComparison.Ordinal)
            ? JsonSerializer.Serialize(profile)
            : null;
    }

    public SessionCoordinator(CutsceneDetector cutscenes, TalkObserver talk, IClientState client, ICondition condition,
        IFramework framework,
        SpeakerResolver speakers, QuestDialoguePrefetcher prefetcher, MsqProgressReader msqProgress,
        CutsceneVoiceManifestProvider cutsceneVoices,
        LipSyncService lipSync,
        Database database, VoiceRegistry voices, BootstrapService bootstrap,
        string cacheDirectory,
        string debugAudioDirectory,
        CastingProfileCatalog catalog,
        OfficialVoiceCatalog officialVoiceCatalog,
        Func<uint, string?> territoryPlaceName,
        Configuration configuration, IPluginLog log,
        IGameMixerAudioBackend? gameMixerBackend = null)
    {
        this.cutscenes = cutscenes;
        this.talk = talk;
        this.client = client;
        this.condition = condition;
        this.framework = framework;
        this.speakers = speakers;
        this.prefetcher = prefetcher;
        this.msqProgress = msqProgress;
        this.cutsceneVoices = cutsceneVoices;
        this.lipSync = lipSync;
        this.voices = voices;
        this.debugAudioDirectory = debugAudioDirectory;
        this.catalog = catalog;
        this.officialVoiceCatalog = officialVoiceCatalog;
        this.territoryPlaceName = territoryPlaceName;
        this.gameMixerBackend = gameMixerBackend;
        this.bootstrap = bootstrap;
        this.configuration = configuration;
        this.log = log;
        debugBaseVoices = officialVoiceCatalog.Groups
            .Select(value => new DebugBaseVoiceOption(value.Id, value.Label, false)).ToArray();
        lineCache = new LineCache(database, cacheDirectory, () => configuration.CacheLimitBytes);
        lineCache.Failed += error => log.Warning(error, "Line cache operation failed");

        cutscenes.Started += OnCutsceneStarted;
        cutscenes.Ended += OnCutsceneEnded;
        talk.LineChanged += OnLineChanged;
        talk.PresentationReady += OnTalkPresentationReady;
        talk.AutoAdvanceReceiveObserved += OnAutoAdvanceReceiveObserved;
        talk.AutoAdvanceUiObserved += OnAutoAdvanceUiObserved;
        talk.Advanced += OnTalkClosed;
        talk.Hidden += OnTalkClosed;
        talk.Finalized += OnTalkClosed;
        client.TerritoryChanged += OnTerritoryChanged;
        client.Logout += OnLogout;
        condition.ConditionChange += OnConditionChange;
        bootstrap.Ready += OnRuntimeReady;
        bootstrap.VoiceDesignReady += OnVoiceDesignReady;
        // OnLineChangedOnFramework also creates a missing active-cutscene
        // session. This covers plugin reloads and condition/territory event
        // ordering without dropping the Talk line that exposed the gap.
    }

    public bool ShouldSuppressAutomaticAdvance
    {
        get
        {
            if (session is null || Volatile.Read(ref suppressAutomaticAdvance) == 0) return false;
            return talk.Current is not { } current
                   || !ShouldPreserveGameControlledPacing(current.Serial);
        }
    }

    public AudioBackendStatus GetAudioBackendStatus()
    {
        var backend = gameMixerBackend;
        return new AudioBackendStatus(
            audioBackendSession.ActiveBackend,
            true,
            backend?.IsAvailable == true,
            backend?.IsHealthy == true,
            audioBackendSession.IsSceneLocked,
            backend?.Diagnostic ?? "FFXIV game mixer backend is not installed");
    }

    public void NotifyOfficialProfilePackImported() => QueueFrameworkAction(() =>
    {
        profileCache.Clear();
        ScheduleDebugBaseVoiceRefresh(CurrentLanguage());
        completedPreDubPlanKey = null;
        if (!cutscenes.IsInCutscene) cutsceneVoices.Reset();
        SchedulePreDubOnFramework();
    }, "Official profile pack cache refresh failed");

    public void NotifyPreDubConfigurationChanged() => QueueFrameworkAction(() =>
    {
        if (configuration.Enabled && configuration.PreDubUpcomingCutscenes)
        {
            SchedulePreDubOnFramework();
            return;
        }
        CancelPreDubOnFramework();
        UpdateBaseHotLoadSafetyOnFramework();
        RequestBaseHotLoadRestore();
    }, "Pre-dub configuration refresh failed");

    private void OnRuntimeReady(RuntimeManager manager) => QueueFrameworkAction(
        () => OnRuntimeReadyOnFramework(manager), "Runtime-ready framework dispatch failed");

    private void OnRuntimeReadyOnFramework(RuntimeManager manager)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        runtimeManager = manager;
        manager.SetBaseHotLoadSafetyPredicate(() => new BaseHotLoadSafety(
            Volatile.Read(ref baseHotLoadSafe) != 0,
            Volatile.Read(ref baseHotLoadSafetyGeneration)));
        manager.SelectionChanged += OnBackendSelectionChanged;
        audio ??= new AudioEngine(gameMixerBackend);
        audio.Started += lipSync.Start;
        audio.Finished += _ => lipSync.Stop();
        audio.Started += line => QueueFrameworkAction(() =>
        {
            IsSpeaking = true;
            LineStarted?.Invoke(line);
        }, "Audio-start framework dispatch failed");
        audio.Finished += line => QueueFrameworkAction(() =>
        {
            IsSpeaking = false;
            LineFinished?.Invoke(line);
        }, "Audio-finish notification framework dispatch failed");
        audio.Finished += line =>
        {
            CompleteDebugPlayback(line);
            QueueFrameworkAction(() => OnAudioFinished(line), "Audio-finished framework dispatch failed");
        };
        audio.Failed += (line, error) =>
        {
            CompleteDebugPlayback(line, error);
            QueueFrameworkAction(() => OnAudioFailed(line, error), "Audio-failure framework dispatch failed");
        };
        audio.Started += line => QueueFrameworkAction(() =>
        {
            if (line.SourceQuest == DebugLineSource) Volatile.Write(ref debugPlaybackActive, 1);
        }, "Debug audio-start framework dispatch failed");
        audio.Finished += line => QueueFrameworkAction(() =>
        {
            if (line.SourceQuest != DebugLineSource) return;
            Volatile.Write(ref debugPlaybackActive, 0);
            if (line.State == DubLineState.Completed) Volatile.Write(ref debugStatus, $"{line.SpeakerName}: playback passed");
            line.Dispose();
        }, "Debug audio-finish framework dispatch failed");
        audio.Finished += line => QueueFrameworkAction(() =>
        {
            if (line.SourceQuest != DebugLineSource || audioBackendSession.IsSceneLocked) return;
            audioBackendSession.EndDebug();
        }, "Debug audio session restore dispatch failed");
        var language = CurrentLanguage();
        Volatile.Write(ref frameworkLanguage, language);
        scheduler = new DubScheduler(
            manager.Runtime,
            ResolveVoiceAsync,
            lineCache,
            manager.ModelHash,
            language,
            () => Math.Min(Math.Max(0, configuration.CacheLimitBytes), 256L * 1024 * 1024));
        scheduler.LineBuffered += line => QueueFrameworkAction(
            () => OnLineBuffered(line), "Buffered-line framework dispatch failed");
        scheduler.PredictionStreamable += _ => QueueFrameworkAction(
            TryCompleteAutoAdvance, "Prediction-streamable framework dispatch failed");
        scheduler.LineFailed += (line, error) =>
        {
            log.Error(error, "Synthesis failed for line {Serial}", line.Sequence);
            QueueFrameworkAction(() =>
            {
                if (line.ActualStatus == ActualStatus.Actual && session?.Epoch == line.SessionEpoch)
                    Volatile.Write(ref suppressAutomaticAdvance, 0);
            }, "Failed-line auto-advance release dispatch failed");
        };
        scheduler.LineProcessed += OnSchedulerLineProcessed;
        scheduler.BecameIdle += () => QueueFrameworkAction(
            OnSchedulerIdle, "Scheduler-idle framework dispatch failed");
        UpdateBaseHotLoadSafetyOnFramework();
        SchedulePreDubOnFramework();
    }

    private void OnVoiceDesignReady(string designPath, string codecPath)
    {
        var manager = bootstrap.RuntimeManager;
        if (manager is null || Volatile.Read(ref disposed) != 0) return;
        voiceDesignPath = designPath;
        voiceDesignCodecPath = codecPath;
        QueueFrameworkAction(() => InitializeDomainPoolOnFramework(manager),
            "Casting-domain pool initialization failed");
    }

    private void InitializeDomainPoolOnFramework(RuntimeManager manager)
    {
        if (Volatile.Read(ref disposed) != 0 || domainPool is not null) return;
        var pool = new CastingDomainPool(
            voices,
            catalog,
            () => voiceDesigner,
            () => true,
            () => territoryPlaceName(client.TerritoryType),
            CurrentLanguage,
            () => (configuration.ReadyMasculineVoices, configuration.ReadyFeminineVoices),
            manager.ModelHash,
            configuration.GetPromptOverride,
            () => configuration.BackgroundCasting,
            async token => (await CaptureFrameworkStateAsync(token).ConfigureAwait(false)).CanWork,
            async token => (await CaptureFrameworkStateAsync(token).ConfigureAwait(false)).TerritoryPlaceName,
            async token => (await CaptureFrameworkStateAsync(token).ConfigureAwait(false)).Language,
            async (instruction, seed, language, token) =>
            {
                var designer = await EnsureVoiceDesignerAsync(token).ConfigureAwait(false);
                return await designer.DesignReferenceAsync(instruction, seed, language, token)
                    .ConfigureAwait(false);
            });
        pool.Failed += error => log.Warning(error, "Background voice casting failed; retrying");
        domainPool = pool;
        pool.ActivateTerritory(territoryPlaceName(client.TerritoryType));
        RequestBaseHotLoadRestore();
    }

    private Task<VoiceDesigner> EnsureVoiceDesignerAsync(CancellationToken token)
    {
        Task<VoiceDesigner> initialization;
        lock (coordinatorTaskGate)
        {
            if (Volatile.Read(ref disposed) != 0)
                return Task.FromException<VoiceDesigner>(new ObjectDisposedException(nameof(SessionCoordinator)));
            if (voiceDesigner is { } ready) return Task.FromResult(ready);
            if (voiceDesignerInitialization is { } existing) return existing.WaitAsync(token);
            var designPath = voiceDesignPath
                ?? throw new InvalidOperationException("VoiceDesign model is still downloading");
            var codecPath = voiceDesignCodecPath
                ?? throw new InvalidOperationException("VoiceDesign codec is still downloading");
            initialization = Task.Run(async () =>
            {
                VoiceDesigner? created = null;
                try
                {
                    var manager = runtimeManager
                        ?? throw new InvalidOperationException("Inference runtime is not initialized");
                    var attemptedBackend = manager.Selection?.Effective.Name
                        ?? throw new InvalidOperationException("No inference device is selected");
                    while (true)
                    {
                        try
                        {
                            created = new VoiceDesigner(manager.Runtime, designPath, codecPath, attemptedBackend,
                                manager.PluginLifetimeLease,
                                manager.ExtractReferenceAsync);
                            var latestBackend = manager.Selection?.Effective.Name;
                            if (latestBackend is not null && latestBackend != attemptedBackend)
                            {
                                attemptedBackend = latestBackend;
                                await created.SwitchBackendAsync(
                                    attemptedBackend, officialObservationShutdown.Token).ConfigureAwait(false);
                            }
                            break;
                        }
                        catch (Exception backendError) when (backendError is not OperationCanceledException)
                        {
                            if (created is not null)
                            {
                                await created.DisposeAsync().ConfigureAwait(false);
                                created = null;
                            }
                            log.Warning(backendError,
                                "VoiceDesign rejected backend {Backend} during initialization", attemptedBackend);
                            var fallback = await manager.RejectBackendAsync(
                                attemptedBackend, backendError, officialObservationShutdown.Token).ConfigureAwait(false);
                            if (String.Equals(fallback.Effective.Name, attemptedBackend, StringComparison.Ordinal))
                                throw;
                            attemptedBackend = fallback.Effective.Name;
                        }
                    }
                    if (Volatile.Read(ref disposed) != 0)
                    {
                        await created.DisposeAsync().ConfigureAwait(false);
                        throw new ObjectDisposedException(nameof(SessionCoordinator));
                    }
                    voiceDesigner = created;
                    return created;
                }
                catch
                {
                    if (created is not null && !ReferenceEquals(voiceDesigner, created))
                        await created.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            });
            voiceDesignerInitialization = initialization;
            coordinatorTasks.Add(initialization);
        }
        _ = initialization.ContinueWith(task =>
        {
            if (task.IsFaulted) log.Warning(task.Exception!.GetBaseException(),
                "VoiceDesign initialization task failed");
            lock (coordinatorTaskGate)
            {
                coordinatorTasks.Remove(task);
                if (task.IsFaulted || task.IsCanceled)
                    if (ReferenceEquals(voiceDesignerInitialization, task))
                        voiceDesignerInitialization = null;
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return initialization.WaitAsync(token);
    }

    private void OnBackendSelectionChanged(BackendSelection selection)
    {
        VoiceDesigner? designer;
        TaskCompletionSource completion;
        lock (backendSwitchGate)
        {
            designer = voiceDesigner;
            if (designer is null || Volatile.Read(ref disposed) != 0) return;
            if (backendSwitchTask is not null) return;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backendSwitchTask = completion.Task;
        }
        if (designer is null) return;
        _ = SwitchVoiceDesignBackendAsync(designer, selection.Effective.Name, completion);
    }

    private async Task SwitchVoiceDesignBackendAsync(
        VoiceDesigner designer, string backendName, TaskCompletionSource completion)
    {
        try
        {
            await Task.Yield();
            await debugGate.WaitAsync(officialObservationShutdown.Token).ConfigureAwait(false);
            try
            {
                await designer.SwitchBackendAsync(backendName, officialObservationShutdown.Token)
                    .ConfigureAwait(false);
            }
            finally { debugGate.Release(); }
        }
        catch (OperationCanceledException) when (officialObservationShutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            log.Error(error, "VoiceDesign backend migration failed");
            var manager = runtimeManager;
            if (manager is not null && Volatile.Read(ref disposed) == 0)
            {
                try
                {
                    var failedBackend = backendName;
                    var backendError = error;
                    while (true)
                    {
                        var fallback = await manager.RejectBackendAsync(
                            failedBackend, backendError, officialObservationShutdown.Token).ConfigureAwait(false);
                        try
                        {
                            await designer.SwitchBackendAsync(
                                fallback.Effective.Name, officialObservationShutdown.Token).ConfigureAwait(false);
                            log.Warning(
                                "VoiceDesign rejected backend {Backend}; using {Fallback}",
                                backendName, fallback.Effective.Name);
                            break;
                        }
                        catch (Exception nextError) when (nextError is not OperationCanceledException)
                        {
                            failedBackend = fallback.Effective.Name;
                            backendError = nextError;
                            log.Warning(nextError,
                                "VoiceDesign also rejected fallback backend {Backend}", failedBackend);
                        }
                    }
                }
                catch (OperationCanceledException) when (officialObservationShutdown.IsCancellationRequested) { }
                catch (Exception fallbackError)
                {
                    log.Error(fallbackError, "VoiceDesign backend fallback failed");
                }
            }
        }
        finally
        {
            lock (backendSwitchGate)
            {
                completion.TrySetResult();
                if (ReferenceEquals(backendSwitchTask, completion.Task)) backendSwitchTask = null;
            }
        }
    }

    private void OnCutsceneStarted() => QueueFrameworkAction(
        OnCutsceneStartedOnFramework, "Cutscene-start framework dispatch failed");

    private void OnCutsceneStartedOnFramework()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        CancelPreDubOnFramework();
        InvalidateBaseHotLoadSafetyOnFramework();
        domainPool?.Pause();
        domainPool?.ResetActivation();
        domainPool?.ActivateTerritory(territoryPlaceName(client.TerritoryType));
        CancelSession();
        SelectSceneAudioBackend();
        session = new CutsceneSession(Interlocked.Increment(ref nextEpoch), client.TerritoryType);
        cutsceneVoices.Reset();
        cutsceneSpeakerKeys.Clear();
        cutsceneSpeakerAssignments.Clear();
        prefetcher.BeginSession();
        log.Information("Cutscene synthesis session started Epoch={Epoch} Territory={Territory}",
            session.Epoch, session.TerritoryId);
    }

    private void OnCutsceneEnded() => QueueFrameworkAction(
        OnCutsceneEndedOnFramework, "Cutscene-end framework dispatch failed");

    private void OnCutsceneEndedOnFramework()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        domainPool?.Pause();
        cutsceneVoices.Reset();
        CancelSession();
        EndSceneAudioBackend();
        UpdateBaseHotLoadSafetyOnFramework();
        RequestBaseHotLoadRestore();
        SchedulePreDubOnFramework();
    }

    private void OnTerritoryChanged(uint territory) => QueueFrameworkAction(
        () => OnTerritoryChangedOnFramework(territory), "Territory-change framework dispatch failed");

    private void OnTerritoryChangedOnFramework(uint territory)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (cutscenes.IsInCutscene)
        {
            log.Information("Restarting cutscene synthesis session after territory change Territory={Territory}",
                territory);
            OnCutsceneStartedOnFramework();
            return;
        }
        CancelPreDubOnFramework();
        InvalidateBaseHotLoadSafetyOnFramework();
        domainPool?.Pause();
        domainPool?.ResetActivation();
        domainPool?.ActivateTerritory(territoryPlaceName(territory));
        CancelSession();
        if (!cutscenes.IsInCutscene) EndSceneAudioBackend();
        UpdateBaseHotLoadSafetyOnFramework();
        RequestBaseHotLoadRestore();
        SchedulePreDubOnFramework();
    }

    private void OnLogout(int _, int __) => QueueFrameworkAction(
        OnLogoutOnFramework, "Logout framework dispatch failed");

    private void OnLogoutOnFramework()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        InvalidateBaseHotLoadSafetyOnFramework();
        domainPool?.Pause();
        domainPool?.ResetActivation();
        CancelPreDubOnFramework();
        CancelSession();
        EndSceneAudioBackend();
    }

    private void OnConditionChange(ConditionFlag flag, bool value) => QueueFrameworkAction(
        () => OnConditionChangeOnFramework(flag, value), "Condition-change framework dispatch failed");

    private void OnConditionChangeOnFramework(ConditionFlag flag, bool value)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (flag != ConditionFlag.InCombat) return;
        if (value)
        {
            CancelPreDubOnFramework();
            InvalidateBaseHotLoadSafetyOnFramework();
        }
        else
        {
            UpdateBaseHotLoadSafetyOnFramework();
            RequestBaseHotLoadRestore();
            SchedulePreDubOnFramework();
        }
    }

    private void SelectSceneAudioBackend()
    {
        audioBackendSession.SelectForScene();
        var status = GetAudioBackendStatus();
        log.Information(
            "Scene audio backend locked Backend={Backend} Requested={Requested} Available={Available} Healthy={Healthy} Diagnostic={Diagnostic}",
            status.ActiveBackend, status.Configured, status.Available, status.Healthy, status.Diagnostic);
    }

    private void EndSceneAudioBackend()
    {
        audioBackendSession.EndScene();
    }

    private void OnSchedulerIdle()
    {
        UpdateBaseHotLoadSafetyOnFramework();
        RequestBaseHotLoadRestore();
    }

    private void SchedulePreDubOnFramework()
    {
        var manager = runtimeManager;
        if (Volatile.Read(ref disposed) != 0 || !configuration.Enabled
            || !configuration.PreDubUpcomingCutscenes
            || cutscenes.IsInCutscene || condition[ConditionFlag.InCombat]
            || scheduler is null || manager is null || !manager.IsReady
            || bootstrap.State != BootstrapState.Ready || manager.IsSwitching
            || Volatile.Read(ref debugRunning) != 0 || Volatile.Read(ref debugPlaybackActive) != 0
            || Volatile.Read(ref exclusiveOperationActive) != 0)
            return;

        IReadOnlyList<uint> candidates;
        try { candidates = msqProgress.GetUpcomingTrackedCutscenes(PreDubCandidateLimit); }
        catch (Exception error)
        {
            log.Warning(error, "MSQ pre-dub frontier could not be read");
            return;
        }

        var scenes = new List<(uint CutsceneId, CutsceneVoiceLine[] Lines)>();
        foreach (var cutsceneId in candidates)
        {
            var manifest = cutsceneVoices.GetManifest(cutsceneId);
            if (manifest is null) continue;
            var synthetic = manifest.Lines
                .Where(line => !line.IsVoiced && !line.IsPlayerChoice)
                .ToArray();
            if (synthetic.Length == 0) continue;
            scenes.Add((cutsceneId, synthetic));
            if (scenes.Count == PreDubSceneLimit) break;
        }
        if (scenes.Count == 0) return;

        var language = CurrentLanguage();
        var planKey = $"{manager.ModelHash}\0{language}\0{String.Join(",", scenes.Select(scene => scene.CutsceneId))}";
        if (preDubSession is not null)
        {
            if (String.Equals(preDubPlanKey, planKey, StringComparison.Ordinal)) return;
            CancelPreDubOnFramework();
        }
        if (String.Equals(completedPreDubPlanKey, planKey, StringComparison.Ordinal)) return;

        var unresolved = 0;
        var predictions = scenes.SelectMany(scene => scene.Lines.Select(line => (scene.CutsceneId, Line: line)))
            .Select(value =>
            {
                var group = value.Line.OfficialGroupId is { Length: > 0 }
                    ? officialVoiceCatalog.GetGroup(value.Line.OfficialGroupId)
                    : officialVoiceCatalog.Resolve(
                        value.Line.ActorNpcBaseId, value.Line.ActorToken, language);
                if (group is null)
                {
                    unresolved++;
                    return null;
                }
                return new CutscenePrediction(
                    $"pre-dub:{value.CutsceneId}:{value.Line.NodeId}",
                    OfficialVoiceCatalog.CanonicalSpeakerKey(group.Id),
                    group.Label,
                    value.Line.Text,
                    language,
                    group.Id,
                    SourceQuest: PreDubLineSource);
            })
            .Where(value => value is not null)
            .Select(value => value!)
            .ToArray();
        if (predictions.Length == 0)
        {
            log.Debug(
                "MSQ pre-dub found synthetic scenes but no stable voices Scenes={Scenes} Lines={Lines}",
                String.Join(",", scenes.Select(scene => scene.CutsceneId)), unresolved);
            return;
        }

        domainPool?.Pause();
        var preDub = new CutsceneSession(
            Interlocked.Increment(ref nextEpoch), client.TerritoryType);
        var added = preDub.ReconcilePredictions(predictions, preserveExisting: false);
        if (added.Count == 0)
        {
            preDub.Dispose();
            return;
        }
        preDubSession = preDub;
        preDubPlanKey = planKey;
        preDubLinesRemaining = added.Count;
        preDubFailed = false;
        EnsureCutsceneBaseResidency();
        InvalidateBaseHotLoadSafetyOnFramework();
        foreach (var line in added) scheduler.Enqueue(line);
        log.Information(
            "MSQ pre-dub scheduled Scenes={Scenes} Lines={Lines} DeferredActors={DeferredActors}",
            String.Join(",", scenes.Select(scene => scene.CutsceneId)), added.Count, unresolved);
    }

    private void OnSchedulerLineProcessed(DubLine line)
    {
        if (!String.Equals(line.SourceQuest, PreDubLineSource, StringComparison.Ordinal)) return;
        QueueFrameworkAction(() => OnPreDubLineProcessedOnFramework(line),
            "Pre-dub completion framework dispatch failed");
    }

    private void OnPreDubLineProcessedOnFramework(DubLine line)
    {
        var current = preDubSession;
        if (current is null || current.Epoch != line.SessionEpoch) return;
        if (line.State != DubLineState.Buffered) preDubFailed = true;
        current.ReleaseLine(line.Sequence);
        if (--preDubLinesRemaining > 0) return;

        var completedKey = preDubPlanKey;
        var failed = preDubFailed;
        current.Dispose();
        preDubSession = null;
        preDubPlanKey = null;
        preDubLinesRemaining = 0;
        preDubFailed = false;
        if (!failed) completedPreDubPlanKey = completedKey;
        ReleaseCutsceneBaseResidency();
        UpdateBaseHotLoadSafetyOnFramework();
        RequestBaseHotLoadRestore();
        if (failed)
            log.Warning("MSQ pre-dub stopped with one or more uncached lines; retry deferred until next safe-state change");
        else
            log.Information("MSQ pre-dub completed Plan={Plan}", completedKey ?? String.Empty);
    }

    private void CancelPreDubOnFramework()
    {
        var current = preDubSession;
        if (current is null) return;
        scheduler?.InvalidateEpoch(current.Epoch);
        current.Dispose();
        preDubSession = null;
        preDubPlanKey = null;
        preDubLinesRemaining = 0;
        preDubFailed = false;
        ReleaseCutsceneBaseResidency();
    }

    private void UpdateBaseHotLoadSafetyOnFramework()
    {
        var safe = !cutscenes.IsInCutscene
                   && !condition[ConditionFlag.InCombat]
                   && preDubSession is null
                   && scheduler?.HasUrgentWork != true
                   && bootstrap.State == BootstrapState.Ready
                   && runtimeManager?.IsSwitching != true
                   && Volatile.Read(ref debugRunning) == 0
                   && Volatile.Read(ref debugPlaybackActive) == 0
                   && Volatile.Read(ref exclusiveOperationActive) == 0;
        var next = safe ? 1 : 0;
        var previous = Interlocked.Exchange(ref baseHotLoadSafe, next);
        if (previous != next) Interlocked.Increment(ref baseHotLoadSafetyGeneration);
        if (!safe) runtimeManager?.CancelBaseHotLoadRestore();
    }

    private void InvalidateBaseHotLoadSafetyOnFramework()
    {
        Volatile.Write(ref baseHotLoadSafe, 0);
        Interlocked.Increment(ref baseHotLoadSafetyGeneration);
        runtimeManager?.CancelBaseHotLoadRestore();
    }

    private void RequestBaseHotLoadRestore()
    {
        if (Volatile.Read(ref disposed) != 0 || !configuration.KeepBaseModelLoaded) return;
        var manager = runtimeManager;
        if (manager is null) return;
        Task restore;
        try { restore = manager.EnsureBaseHotLoadedWhenSafeAsync(officialObservationShutdown.Token); }
        catch (Exception error)
        {
            log.Warning(error, "Base hot-load restore could not be scheduled");
            return;
        }
        lock (coordinatorTaskGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            coordinatorTasks.Add(restore);
        }
        _ = ObserveBaseHotLoadRestoreAsync(restore);
    }

    private async Task ObserveBaseHotLoadRestoreAsync(Task restore)
    {
        try { await restore.ConfigureAwait(false); }
        catch (OperationCanceledException) when (officialObservationShutdown.IsCancellationRequested) { }
        catch (Exception error) { log.Warning(error, "Base hot-load restore failed"); }
        finally
        {
            lock (coordinatorTaskGate) coordinatorTasks.Remove(restore);
        }
    }

    private void OnLineChanged(ActualTalkLine line) => QueueFrameworkAction(
        () => OnLineChangedOnFramework(line), "Talk-line framework dispatch failed");

    private void OnLineChangedOnFramework(ActualTalkLine line)
    {
        if (Volatile.Read(ref disposed) != 0 || !configuration.Enabled
            || (!cutscenes.IsInCutscene && scheduler?.HasUrgentWork != true)) return;
        if (session is null && cutscenes.IsInCutscene)
        {
            log.Warning("Recovering missing cutscene synthesis session from Talk line Serial={Serial}",
                line.Serial);
            OnCutsceneStartedOnFramework();
        }
        if (session is null || scheduler is null) return;
        Interlocked.Increment(ref talkIdleGeneration);
        InvalidateBaseHotLoadSafetyOnFramework();
        Volatile.Write(ref gameControlledAdvanceSerial, 0);
        Volatile.Write(ref talkAdvanceContext,
            new(line.Serial, cutsceneVoices.IsCurrentCutsceneUnskippable()));
        // Own autoplay from the first observed frame. CUTB/native detection
        // releases it for player choices or genuine native VO; synthetic
        // resolution must not race the game's text-speed timer.
        Volatile.Write(ref suppressAutomaticAdvance, 1);
        var capturedSession = session;
        ResolvedSpeaker capturedResolved;
        CutsceneVoiceLine? declaredVoice;
        string language;
        string? firstTerritory;
        try
        {
            capturedResolved = SnapshotResolvedSpeaker(speakers.Resolve(line, capturedSession.Epoch));
            language = CurrentLanguage();
            firstTerritory = territoryPlaceName(capturedSession.TerritoryId);
            declaredVoice = cutsceneVoices.Resolve(line);
            if (declaredVoice is null)
            {
                LogCutb(
                    "CUTB lookup missed TalkSerial={TalkSerial} Reason={Reason}",
                    line.Serial, cutsceneVoices.LastStatus);
                // A manifest miss removes lookahead authority, not the current
                // Talk line.  Continue through exact live-speaker/profile
                // resolution and release autoplay only if that resolution or
                // synthesis actually fails.  Clearing predictions here also
                // discarded already-prepared work after a recoverable miss.
            }
            if (declaredVoice is { IsVoiced: true })
            {
                var declaredGroup = declaredVoice.OfficialGroupId is null
                    ? null
                    : officialVoiceCatalog.GetGroup(declaredVoice.OfficialGroupId);
                declaredGroup ??= officialVoiceCatalog.Resolve(
                    capturedResolved.NpcBaseId, declaredVoice.ActorToken, language)
                    ?? officialVoiceCatalog.Resolve(
                        capturedResolved.NpcBaseId, capturedResolved.DisplayName, language);
                if (declaredGroup is not null)
                    capturedResolved = CanonicalizeOfficialAlias(capturedResolved, declaredGroup);
            }
        }
        catch (Exception error)
        {
            Volatile.Write(ref suppressAutomaticAdvance, 0);
            log.Warning(error, "Talk line ignored because framework speaker snapshot failed");
            return;
        }
        // Publish the immutable actor/evidence snapshot before asynchronous
        // line promotion starts. Native clips can arrive in that interval;
        // they must never reread a mutable actor or same-name replacement.
        audio?.Stop();
        lipSync.Stop();
        Interlocked.Exchange(ref pendingAutoAdvance, null);
        CancelAutoAdvanceRetry();
        CancelActualLines();
        if (declaredVoice is { IsPlayerChoice: true })
        {
            Volatile.Write(ref suppressAutomaticAdvance, 0);
            LogCutb(
                "CUTB declared player choice TalkSerial={Serial} Key={Key}",
                line.Serial, declaredVoice.Key);
            return;
        }
        if (declaredVoice is { IsVoiced: true })
        {
            Volatile.Write(ref suppressAutomaticAdvance, 0);
            LogCutb(
                "CUTB declared native VO TalkSerial={Serial} Key={Key} Actor={Actor}",
                line.Serial, declaredVoice.Key, declaredVoice.ActorToken);
            return;
        }
        QueueLineHandling(line, capturedSession, capturedResolved, language, firstTerritory, declaredVoice);
    }

    private bool ShouldPreserveGameControlledPacing(long talkSerial)
    {
        if (Volatile.Read(ref gameControlledAdvanceSerial) == talkSerial) return true;
        var context = Volatile.Read(ref talkAdvanceContext);
        if (context is null || context.TalkSerial != talkSerial
            || !TalkAdvancePolicy.ShouldPreserveGameControlledPacing(
                context.CutsceneUnskippable,
                talk.IsAutomaticOnlyPresentation(talkSerial))) return false;
        Volatile.Write(ref gameControlledAdvanceSerial, talkSerial);
        if (configuration.AutoAdvanceDiagnostics)
            log.Information(
                "Preserving game-controlled pacing for unskippable automatic-only Talk line Serial={Serial}",
                talkSerial);
        return true;
    }

    private void QueueLineHandling(ActualTalkLine line, CutsceneSession capturedSession,
        ResolvedSpeaker capturedResolved, string language, string? firstTerritory,
        CutsceneVoiceLine? declaredVoice)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (coordinatorTaskGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            coordinatorTasks.Add(completion.Task);
        }
        _ = ObserveLineHandlingAsync(completion, line, capturedSession, capturedResolved, language,
            firstTerritory, declaredVoice);
    }

    private async Task ObserveLineHandlingAsync(TaskCompletionSource completion, ActualTalkLine line,
        CutsceneSession capturedSession, ResolvedSpeaker capturedResolved, string language,
        string? firstTerritory, CutsceneVoiceLine? declaredVoice)
    {
        try
        {
            await HandleLineAsync(line, capturedSession, capturedResolved, language, firstTerritory,
                    declaredVoice)
                .ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
            log.Warning(error, "Talk-line handling task failed");
        }
        finally
        {
            lock (coordinatorTaskGate) coordinatorTasks.Remove(completion.Task);
        }
    }

    private async Task HandleLineAsync(
        ActualTalkLine actual, CutsceneSession capturedSession, ResolvedSpeaker capturedResolved,
        string language, string? firstTerritory, CutsceneVoiceLine? declaredVoice)
    {
        using var lineCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            capturedSession.CancellationToken, officialObservationShutdown.Token);
        var operationToken = lineCancellation.Token;
        await eventGate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0) return;
            var current = capturedSession;
            var currentScheduler = scheduler;
            if (currentScheduler is null || !IsCurrent(current)) return;
            var resolved = capturedResolved;
            var voiceSex = catalog.ResolveVoiceSex(resolved.Evidence, resolved.Sex);
            if (!String.Equals(voiceSex, resolved.Sex, StringComparison.Ordinal))
                resolved = resolved with
                {
                    Sex = voiceSex,
                    Archetype = voiceSex == "feminine" ? "feminine_adult" : "masculine_adult",
                    Evidence = resolved.Evidence with { Sex = voiceSex },
                    Metadata = resolved.Metadata with { Sex = voiceSex },
                };
            // Curated official identities span many live ENpcBase rows. Resolve
            // them before durable speaker lookup so every variant shares one
            // Base profile. Arbitrary unresolved scene-local names stay transient.
            var officialGroup = declaredVoice?.OfficialGroupId is null
                ? null
                : officialVoiceCatalog.GetGroup(declaredVoice.OfficialGroupId);
            officialGroup ??= declaredVoice is null
                ? null
                : officialVoiceCatalog.Resolve(
                    declaredVoice.ActorNpcBaseId ?? resolved.NpcBaseId,
                    declaredVoice.ActorToken, language);
            officialGroup ??= officialVoiceCatalog.Resolve(
                resolved.NpcBaseId, resolved.DisplayName, language);
            if (officialGroup is not null)
                resolved = CanonicalizeOfficialAlias(resolved, officialGroup);
            var stored = resolved.SceneLocal
                ? null
                : await voices.ResolveSpeakerAsync(
                    resolved.StableKey, resolved.NpcBaseId, resolved.DisplayName,
                    current.TerritoryId, language, resolved.Metadata, operationToken).ConfigureAwait(false);
            if (!IsCurrent(current)) return;
            if (stored is not null)
            {
                speakerKeys[stored.Id] = stored.StableKey;
                await AttachShippedOfficialProfileAsync(stored.Id, resolved, officialGroup, language,
                    operationToken).ConfigureAwait(false);
            }
            if (officialGroup is not null && stored is not null
                && await voices.GetBestVoiceAsync(stored.Id, language,
                    (runtimeManager ?? throw new InvalidOperationException("Base runtime is unavailable")).ModelHash,
                    operationToken).ConfigureAwait(false) is null)
            {
                // A known dubbed character without a learned Base reference is
                // intentionally silent. Never manufacture a generic designed
                // identity while native observations are still being learned.
                Volatile.Write(ref suppressAutomaticAdvance, 0);
                return;
            }
            var casting = resolved.SceneLocal
                ? catalog.Resolve(resolved.Evidence)
                : await ResolveCastingAsync(stored!, resolved, current.TerritoryId, firstTerritory,
                    operationToken)
                    .ConfigureAwait(false);
            if (!IsCurrent(current)) return;
            if (!resolved.SceneLocal) domainPool?.ActivateResolution(casting);
            var slot = catalog.SelectBestSlot(casting, resolved.Evidence);
            var assignment = new ResolvedLineSpeaker(
                stored?.StableKey ?? resolved.StableKey,
                actual.Speaker,
                stored?.Id ?? 0,
                resolved.Evidence,
                casting,
                resolved.Sex,
                resolved.Archetype,
                resolved.ActorAddress,
                slot.Id,
                language);
            var line = current.PromotePrediction(
                actual.Speaker, actual.Text, assignment, predictionKey: declaredVoice?.Key);
            var promotedPrediction = line is not null;
            if (line is null)
            {
                line = current.AddActual(stored?.StableKey ?? resolved.StableKey,
                    stored?.DisplayName ?? resolved.DisplayName, actual.Text, language);
                line.ApplyResolvedSpeaker(assignment);
            }
            else
            {
                currentScheduler.PromoteActual(current.Epoch, line.Sequence, DateTimeOffset.UtcNow);
            }
            line.TransientSpeaker = resolved.SceneLocal;
            if (resolved.SceneLocal) line.SpeakerId = null;
            line.ActualTalkSerial = actual.Serial;
            cutsceneSpeakerKeys[NormalizeSpeakerToken(actual.Speaker)] = assignment.SpeakerKey;
            cutsceneSpeakerAssignments[NormalizeSpeakerToken(actual.Speaker)] = assignment;
            if (declaredVoice is not null)
            {
                cutsceneSpeakerKeys[NormalizeSpeakerToken(declaredVoice.ActorToken)] = assignment.SpeakerKey;
                cutsceneSpeakerAssignments[NormalizeSpeakerToken(declaredVoice.ActorToken)] = assignment;
                if (declaredVoice.ActorNpcBaseId is { } actorNpcBaseId)
                {
                    cutsceneSpeakerKeys[$"npc:{actorNpcBaseId}"] = assignment.SpeakerKey;
                    cutsceneSpeakerAssignments[$"npc:{actorNpcBaseId}"] = assignment;
                }
            }
            if (!line.TryMarkNotVoiced()) return;
            EnsureCutsceneBaseResidency();
            Volatile.Write(ref suppressAutomaticAdvance, 1);
            if (promotedPrediction && line.State == DubLineState.Buffered && line.Audio.ProducerCompleted)
                QueueFrameworkAction(() => OnLineBuffered(line),
                    "Promoted-line framework dispatch failed");
            else if (!promotedPrediction || line.State == DubLineState.Predicted)
                currentScheduler.Enqueue(line);

            if (declaredVoice is not null)
            {
                var immediate = cutsceneVoices.GetImmediateSuccessors(declaredVoice)
                    .Where(value => !value.IsVoiced && !value.IsPlayerChoice)
                    .Select(value => value.Key).Distinct(StringComparer.Ordinal).ToArray();
                line.NextPredictionKeys = immediate;
                line.NextPredictionKey = immediate.Length == 1 ? immediate[0] : null;
                var future = cutsceneVoices.GetSyntheticFuture(declaredVoice)
                    .Select(value => new FutureDialogue(value.Key, value.ActorToken, value.Text,
                        value.OfficialGroupId, value.ActorNpcBaseId))
                    .ToArray();
                await ReconcileFutureDialogueAsync(
                        current, currentScheduler, future, language, firstTerritory,
                        preserveExisting: promotedPrediction, operationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var update = prefetcher.Observe(actual.Speaker, actual.Text);
                if (update.Synchronized)
                {
                    var future = update.Future.Select(value => new FutureDialogue(
                        $"{value.QuestSheet}\0{value.Index}", value.Speaker, value.Text)).ToArray();
                    line.NextPredictionKey = future.FirstOrDefault()?.Key;
                    line.NextPredictionKeys = line.NextPredictionKey is null
                        ? []
                        : [line.NextPredictionKey];
                    await ReconcileFutureDialogueAsync(
                            current, currentScheduler, future, language, firstTerritory,
                            preserveExisting: promotedPrediction, operationToken)
                        .ConfigureAwait(false);
                }
                else if (update.Resynchronized)
                {
                    line.NextPredictionKey = null;
                    line.NextPredictionKeys = [];
                    current.ReconcilePredictions([], preserveExisting: false);
                }
            }
        }
        catch (Exception error) { log.Error(error, "Failed to schedule Talk line"); }
        finally { eventGate.Release(); }
    }

    private async Task ReconcileFutureDialogueAsync(
        CutsceneSession current,
        DubScheduler currentScheduler,
        IReadOnlyList<FutureDialogue> future,
        string language,
        string? firstTerritory,
        bool preserveExisting,
        CancellationToken token)
    {
        var modelHash = (runtimeManager
            ?? throw new InvalidOperationException("Base runtime is unavailable")).ModelHash;
        foreach (var futureActor in future.DistinctBy(FutureActorKey))
        {
            var actorToken = futureActor.ActorToken;
            var normalizedActor = FutureActorKey(futureActor);
            var official = futureActor.OfficialGroupId is { Length: > 0 }
                ? officialVoiceCatalog.GetGroup(futureActor.OfficialGroupId)
                : officialVoiceCatalog.Resolve(futureActor.ActorNpcBaseId, actorToken, language);
            if (official is not null
                || cutsceneSpeakerKeys.ContainsKey(normalizedActor)) continue;
            if (futureActor.ActorNpcBaseId is { } npcBaseId
                && speakers.ResolveNpcBase(npcBaseId,
                    String.IsNullOrWhiteSpace(actorToken) ? $"NPC {npcBaseId}" : actorToken) is { } live)
            {
                var voiceSex = catalog.ResolveVoiceSex(live.Evidence, live.Sex);
                if (!String.Equals(voiceSex, live.Sex, StringComparison.Ordinal))
                    live = live with
                    {
                        Sex = voiceSex,
                        Archetype = voiceSex == "feminine" ? "feminine_adult" : "masculine_adult",
                        Evidence = live.Evidence with { Sex = voiceSex },
                        Metadata = live.Metadata with { Sex = voiceSex },
                    };
                var stored = await voices.ResolveSpeakerAsync(
                    live.StableKey, live.NpcBaseId, live.DisplayName, current.TerritoryId,
                    language, live.Metadata, token).ConfigureAwait(false);
                var resolvedOfficial = officialVoiceCatalog.Resolve(
                    live.NpcBaseId, live.DisplayName, language);
                await AttachShippedOfficialProfileAsync(
                    stored.Id, live, resolvedOfficial, language, token).ConfigureAwait(false);
                var casting = await ResolveCastingAsync(
                    stored, live, current.TerritoryId, firstTerritory, token).ConfigureAwait(false);
                if (!await EnsurePersistentLookaheadVoiceAsync(
                        stored, live, casting, language, modelHash, token).ConfigureAwait(false))
                    continue;
                cutsceneSpeakerKeys[normalizedActor] = stored.StableKey;
                cutsceneSpeakerAssignments[normalizedActor] = new(
                    stored.StableKey, live.DisplayName, stored.Id, live.Evidence, casting,
                    live.Sex, live.Archetype, live.ActorAddress,
                    catalog.SelectBestSlot(casting, live.Evidence).Id, language);
                continue;
            }
            var match = await voices.GetBestVoiceByDisplayNameAsync(
                actorToken, language, modelHash, token).ConfigureAwait(false);
            if (match is null) continue;
            cutsceneSpeakerKeys[normalizedActor] = match.StableKey;
            profileCache[ProfileCacheKey(match.StableKey, language, modelHash)] = match.Profile;
        }
        var predictions = future.Select(value =>
        {
            var official = value.OfficialGroupId is { Length: > 0 }
                ? officialVoiceCatalog.GetGroup(value.OfficialGroupId)
                : officialVoiceCatalog.Resolve(value.ActorNpcBaseId, value.ActorToken, language);
            var normalizedActor = FutureActorKey(value);
            cutsceneSpeakerAssignments.TryGetValue(normalizedActor, out var knownAssignment);
            if (official is null && knownAssignment is null
                && !cutsceneSpeakerKeys.ContainsKey(normalizedActor))
                return null;
            var stableKey = official is not null
                ? OfficialVoiceCatalog.CanonicalSpeakerKey(official.Id)
                : cutsceneSpeakerKeys.TryGetValue(normalizedActor, out var knownKey)
                    ? knownKey
                    : knownAssignment!.SpeakerKey;
            return new CutscenePrediction(
                value.Key,
                stableKey,
                knownAssignment?.SpeakerName ?? official?.Label ?? value.ActorToken,
                value.Text,
                language,
                official?.Id,
                official is null ? knownAssignment : null);
        }).Where(value => value is not null).Select(value => value!).ToArray();
        var added = current.ReconcilePredictions(predictions, preserveExisting);
        foreach (var line in added)
        {
            if (!IsCurrent(current))
            {
                line.Cancel(DubLineState.Invalidated);
                return;
            }
            // Predictions remain in-memory and claim no persistent speaker or pool row.
            currentScheduler.Enqueue(line);
        }
    }

    private static string FutureActorKey(FutureDialogue value) =>
        value.ActorNpcBaseId is { } npcBaseId
            ? $"npc:{npcBaseId}"
            : NormalizeSpeakerToken(value.ActorToken);

    private async ValueTask<VoiceResolution> ResolveVoiceAsync(DubLine line, CancellationToken token)
    {
        var language = line.Language
            ?? throw new InvalidOperationException("Queued line has no resolved dubbing language");
        if (line.TransientSpeaker)
            return await ResolveTransientVoiceAsync(line, language, token).ConfigureAwait(false);
        if (line.ActualStatus == ActualStatus.Predicted)
        {
            var predictedModelHash = (runtimeManager
                ?? throw new InvalidOperationException("Base runtime is unavailable")).ModelHash;
            var predictedCacheKey = ProfileCacheKey(line.SpeakerKey, language, predictedModelHash);
            var predictedProfile = profileCache.TryGetValue(predictedCacheKey, out var predictedCached)
                ? predictedCached
                : await voices.GetBestVoiceByStableKeyAsync(line.SpeakerKey, language,
                    predictedModelHash, token).ConfigureAwait(false);
            if (predictedProfile is null && line.OfficialVoiceGroupId is { } officialGroupId)
            {
                var group = officialVoiceCatalog.GetGroup(officialGroupId)
                            ?? throw new InvalidDataException(
                                $"Unknown predicted official voice group '{officialGroupId}'");
                predictedProfile = await GetShippedOfficialProfileAsync(group, language, token)
                    .ConfigureAwait(false);
                if (predictedProfile is null)
                    throw new InvalidOperationException(
                        $"Official voice profile '{officialGroupId}' is unavailable for {language}");
            }
            if (predictedProfile is not null)
            {
                line.VoiceProfileId = predictedProfile.Id;
                line.VoiceProfileHash = predictedProfile.ProfileHash;
                profileCache[predictedCacheKey] = predictedProfile;
                return VoiceResolution.Ready(predictedProfile.Reference);
            }

            if (line.SpeakerId is > 0)
                throw new InvalidOperationException(
                    $"Persistent lookahead speaker '{line.SpeakerKey}' has no prepared Base profile");

            // A scene-local prediction has no durable speaker assignment by
            // design.  Keep it transient and prepare a reference in memory;
            // returning DeferredPrediction here strands the immediate-next
            // prediction and blocks auto-advance forever.
            line.TransientSpeaker = true;
            line.SpeakerId = null;
            line.Casting ??= catalog.Resolve(line.CastingEvidence ??
                new SpeakerCastingEvidence(line.SpeakerKey, Sex: line.VoiceSex));
            return await ResolveTransientVoiceAsync(line, language, token).ConfigureAwait(false);
        }
        if (line.SpeakerId is not { } speakerId) return VoiceResolution.Ready(null);
        var currentModelHash = (runtimeManager
            ?? throw new InvalidOperationException("Base runtime is unavailable")).ModelHash;
        var cacheKey = ProfileCacheKey(line.SpeakerKey, language, currentModelHash);
        var stored = profileCache.TryGetValue(cacheKey, out var cached)
            ? cached
            : await voices.GetBestVoiceAsync(
                speakerId, language, currentModelHash, token).ConfigureAwait(false);
        if (stored is not null)
        {
            line.VoiceProfileId = stored.Id;
            line.VoiceProfileHash = stored.ProfileHash;
            profileCache[cacheKey] = stored;
            return VoiceResolution.Ready(stored.Reference);
        }
        var casting = line.Casting ?? throw new InvalidOperationException("Actual line has no casting resolution");
        var knownTraits = line.CastingEvidence is null ? null : JsonSerializer.Serialize(line.CastingEvidence);
        var pooled = await voices.TryAssignDomainPoolVoiceAsync(
            speakerId, casting.DomainId, language, line.VoiceSex, knownTraits, currentModelHash, token)
            .ConfigureAwait(false);
        if (pooled is not null)
        {
            line.VoiceProfileId = pooled.Id;
            line.VoiceProfileHash = pooled.ProfileHash;
            profileCache[cacheKey] = pooled;
            QueueProfileUpgradeNotification(line.SpeakerKey, pooled.Id);
            return VoiceResolution.Ready(pooled.Reference);
        }
        var evidence = line.CastingEvidence ?? new SpeakerCastingEvidence(line.SpeakerKey, Sex: line.VoiceSex);
        var fallbackProfile = await voices.GetBestVoiceByStableKeyAsync(
            VoiceRegistry.DomainFallbackSpeakerKey(casting.DomainId,
                catalog.FallbackVariantId(casting, evidence)), language, currentModelHash, token)
            .ConfigureAwait(false);
        if (fallbackProfile is not null)
        {
            fallbackProfile = await voices.SaveAndAssignAsync(speakerId, fallbackProfile, token)
                .ConfigureAwait(false);
            line.VoiceProfileId = fallbackProfile.Id;
            line.VoiceProfileHash = fallbackProfile.ProfileHash;
            profileCache[cacheKey] = fallbackProfile;
            return VoiceResolution.Ready(fallbackProfile.Reference);
        }
        domainPool?.RequestMissingResolution(casting, language, line.VoiceSex, followsSpeaker: true);
        throw new InvalidOperationException(
            $"No prepared Base profile exists for '{line.SpeakerKey}' in domain "
            + $"'{casting.DomainId}' ({language}/{currentModelHash}); VoiceDesign is background-only");
    }

    private async ValueTask<VoiceResolution> ResolveTransientVoiceAsync(
        DubLine line, string language, CancellationToken token)
    {
        var evidence = line.CastingEvidence ?? new SpeakerCastingEvidence(line.SpeakerKey,
            Sex: line.VoiceSex);
        var casting = line.Casting ?? catalog.Resolve(evidence);
        line.Casting = casting;
        var currentModelHash = (runtimeManager
            ?? throw new InvalidOperationException("Base runtime is unavailable")).ModelHash;
        var fallbackProfile = await voices.GetBestVoiceByStableKeyAsync(
            VoiceRegistry.DomainFallbackSpeakerKey(casting.DomainId,
                catalog.FallbackVariantId(casting, evidence)), language, currentModelHash, token)
            .ConfigureAwait(false);
        if (fallbackProfile is not null)
            return VoiceResolution.Ready(fallbackProfile.Reference);
        domainPool?.RequestMissingResolution(casting, language, line.VoiceSex, followsSpeaker: true);
        throw new InvalidOperationException(
            $"No prepared transient Base fallback exists for domain '{casting.DomainId}' "
            + $"({language}/{currentModelHash}); VoiceDesign is background-only");
    }

    private async Task<CastingResolution> ResolveCastingAsync(
        SpeakerIdentity speaker,
        ResolvedSpeaker resolved,
        uint territoryId,
        string? firstTerritory,
        CancellationToken token)
    {
        var evidence = resolved.Evidence with
        {
            FirstTerritoryPlaceName = firstTerritory ?? resolved.Evidence.FirstTerritoryPlaceName,
        };
        var persisted = await voices.GetSpeakerCastingAsync(speaker.Id, token).ConfigureAwait(false);
        if (persisted is { IsStable: true })
        {
            try
            {
                _ = catalog.GetDomain(persisted.DomainId);
                var fallback = catalog.Resolve(evidence);
                var traits = ReadCastingTraits(persisted.VariantTraitsJson);
                return fallback with
                {
                    DomainId = persisted.DomainId,
                    ModifierIds = traits?.ModifierIds ?? fallback.ModifierIds,
                    CandidateDomainIds = [persisted.DomainId],
                };
            }
            catch (KeyNotFoundException)
            {
                // A catalog update must not make an existing assigned profile
                // unusable.  Resolve this line through the current catalog,
                // but preserve the versioned stable row so an assigned voice
                // is never silently recast or persisted under a new domain.
                return catalog.Resolve(evidence);
            }
        }

        var resolution = catalog.Resolve(evidence);
        var slot = catalog.SelectBestSlot(resolution, evidence);
        var traitsJson = JsonSerializer.Serialize(new PersistedCastingTraits(
            resolution.ModifierIds.ToArray(), slot.Id));
        await voices.SaveSpeakerCastingAsync(
            speaker.Id,
            resolution.DomainId,
            traitsJson,
            resolution.SourceName,
            territoryId,
            catalog.Version,
            true,
            token).ConfigureAwait(false);
        return resolution;
    }

    private static PersistedCastingTraits? ReadCastingTraits(string? json)
    {
        if (String.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<PersistedCastingTraits>(json);
        }
        catch (JsonException) { return null; }
    }

    private sealed record PersistedCastingTraits(
        IReadOnlyList<string> ModifierIds,
        string? SlotId);

    private static long StableSeed(string value)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var character in value) { hash ^= character; hash *= prime; }
        return unchecked((long)hash);
    }

    private string CurrentLanguage()
    {
        var language = client.ClientLanguage.ToString().ToLowerInvariant();
        return language is "english" or "japanese" or "german" or "french"
            ? language
            : throw new NotSupportedException($"FFXIV dubbing language '{language}' is not supported");
    }

    private async Task ObserveFrameworkCompletionAsync<T>(
        Task dispatch, TaskCompletionSource<T> completion, string failureMessage)
    {
        Exception? failure = null;
        try { await dispatch.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            completion.TrySetCanceled();
        }
        catch (Exception error)
        {
            failure = error;
            completion.TrySetException(error);
        }
        try { await completion.Task.ConfigureAwait(false); }
        catch (Exception error) { failure ??= error; }
        if (failure is not null && failure is not OperationCanceledException)
            log.Warning(failure is AggregateException aggregate ? aggregate.GetBaseException() : failure,
                failureMessage);
    }

    private async Task ObserveFrameworkCompletionAsync(
        Task dispatch, Task completion, string failureMessage)
    {
        Exception? failure = null;
        try { await dispatch.ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        try { await completion.ConfigureAwait(false); }
        catch (Exception error) { failure ??= error; }
        if (failure is not null && failure is not OperationCanceledException)
            log.Warning(failure is AggregateException aggregate ? aggregate.GetBaseException() : failure,
                failureMessage);
    }

    private Task<FrameworkStateSnapshot> CaptureFrameworkStateAsync(CancellationToken token)
    {
        if (framework.IsFrameworkUnloading)
            return Task.FromCanceled<FrameworkStateSnapshot>(new CancellationToken(true));
        if (framework.IsInFrameworkUpdateThread)
        {
            try { return Task.FromResult(CaptureFrameworkStateOnFramework()); }
            catch (Exception error) { return Task.FromException<FrameworkStateSnapshot>(error); }
        }
        var completion = new TaskCompletionSource<FrameworkStateSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var dispatch = framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    if (framework.IsFrameworkUnloading)
                    {
                        completion.TrySetCanceled();
                        return;
                    }
                    completion.TrySetResult(CaptureFrameworkStateOnFramework());
                }
                catch (Exception error) { completion.TrySetException(error); }
            });
            _ = ObserveFrameworkCompletionAsync(dispatch, completion,
                "Framework state capture dispatch failed");
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
            _ = ObserveFrameworkCompletionAsync(Task.CompletedTask, completion.Task,
                "Framework state capture dispatch failed");
        }
        return completion.Task.WaitAsync(token);
    }

    private FrameworkStateSnapshot CaptureFrameworkStateOnFramework()
    {
        if (Volatile.Read(ref disposed) != 0 || framework.IsFrameworkUnloading)
            throw new OperationCanceledException("Resonance framework state capture was disposed");
        var territoryId = client.TerritoryType;
        var inCutscene = cutscenes.IsInCutscene;
        var inCombat = condition[ConditionFlag.InCombat];
        var manager = runtimeManager;
        var designer = voiceDesigner;
        var canWork = !inCutscene && !inCombat && preDubSession is null
                      && scheduler?.HasUrgentWork != true
                      && bootstrap.State == BootstrapState.Ready
                      && manager?.IsSwitching != true
                      && manager?.IsReady == true
                      && (designer is null || designer.IsReady)
                      && Volatile.Read(ref debugRunning) == 0
                      && Volatile.Read(ref debugPlaybackActive) == 0
                      && Volatile.Read(ref exclusiveOperationActive) == 0;
        UpdateBaseHotLoadSafetyOnFramework();
        var language = CurrentLanguage();
        Volatile.Write(ref frameworkLanguage, language);
        return new FrameworkStateSnapshot(
            language, territoryId, territoryPlaceName(territoryId),
            inCutscene, inCombat, canWork);
    }

    private static string ProfileCacheKey(string stableKey, string language, string modelHash) =>
        $"{stableKey}\0{language}\0{modelHash}";

    private static string NormalizeSpeakerToken(string value) => new(value
        .Where(Char.IsLetterOrDigit)
        .Select(Char.ToLowerInvariant)
        .ToArray());

    private void LogCutb(string message, params object[] args)
    {
        log.Debug($"CUTB: {message}", args);
    }

    private void LogAutoAdvanceDiagnostic(string message, params object[] args)
    {
        if (configuration.AutoAdvanceDiagnostics) log.Information($"Auto-advance diagnostics: {message}", args);
    }

    private void OnAutoAdvanceReceiveObserved(AutoAdvanceReceiveSnapshot snapshot)
    {
        switch (autoAdvanceDiagnostics.ObserveReceive(snapshot))
        {
            case AutoAdvanceReceiveDecision.Suppressed:
                return;
            case AutoAdvanceReceiveDecision.Truncated:
                LogAutoAdvanceDiagnostic("Receive signatures truncated serial={Serial} cap={Cap}",
                    snapshot.TalkSerial?.ToString() ?? "none", AutoAdvanceDiagnosticGate.MaxReceiveSignatures);
                return;
            case AutoAdvanceReceiveDecision.Observed:
                LogAutoAdvanceDiagnostic(
                    "Receive serial={Serial} atkEventType={AtkEventType} eventParam={EventParam} atkEventParam={AtkEventParam} "
                    + "stateType={StateType} stateReturnFlags={StateReturnFlags} stateFlags={StateFlags} "
                    + "mouseButtonId={MouseButtonId} mouseModifier={MouseModifier} mouseX={MouseX} mouseY={MouseY} "
                    + "inputId={InputId} inputState={InputState} inputModifier={InputModifier} decision=observed",
                    snapshot.TalkSerial?.ToString() ?? "none",
                    snapshot.AtkEventType,
                    snapshot.EventParam,
                    snapshot.AtkEventParam?.ToString() ?? "absent",
                    snapshot.EventStateType?.ToString() ?? "absent",
                    snapshot.EventStateReturnFlags?.ToString() ?? "absent",
                    snapshot.EventStateFlags?.ToString() ?? "absent",
                    snapshot.MouseButtonId?.ToString() ?? "absent",
                    snapshot.MouseModifier?.ToString() ?? "absent",
                    snapshot.MouseX?.ToString() ?? "absent",
                    snapshot.MouseY?.ToString() ?? "absent",
                    snapshot.InputId?.ToString() ?? "absent",
                    snapshot.InputState?.ToString() ?? "absent",
                    snapshot.InputModifier?.ToString() ?? "absent");
                return;
            default:
                return;
        }
    }

    private async Task<bool> EnsurePersistentLookaheadVoiceAsync(
        SpeakerIdentity speaker,
        ResolvedSpeaker resolved,
        CastingResolution casting,
        string language,
        string modelHash,
        CancellationToken token)
    {
        var cacheKey = ProfileCacheKey(speaker.StableKey, language, modelHash);
        var profile = profileCache.TryGetValue(cacheKey, out var cached)
            ? cached
            : await voices.GetBestVoiceAsync(speaker.Id, language, modelHash, token).ConfigureAwait(false);
        if (profile is null)
        {
            var knownTraits = JsonSerializer.Serialize(resolved.Evidence);
            profile = await voices.TryAssignDomainPoolVoiceAsync(
                speaker.Id, casting.DomainId, language, resolved.Sex, knownTraits, modelHash, token)
                .ConfigureAwait(false);
        }
        if (profile is null)
        {
            var fallback = await voices.GetBestVoiceByStableKeyAsync(
                VoiceRegistry.DomainFallbackSpeakerKey(casting.DomainId,
                    catalog.FallbackVariantId(casting, resolved.Evidence)),
                language, modelHash, token).ConfigureAwait(false);
            if (fallback is not null)
                profile = await voices.SaveAndAssignAsync(speaker.Id, fallback, token).ConfigureAwait(false);
        }
        if (profile is null)
        {
            domainPool?.RequestMissingResolution(casting, language, resolved.Sex, followsSpeaker: true);
            return false;
        }
        profileCache[cacheKey] = profile;
        return true;
    }

    private void OnAutoAdvanceUiObserved(AutoAdvanceUiSnapshot snapshot)
    {
        if (!autoAdvanceDiagnostics.ObserveUi(snapshot)) return;
        LogAutoAdvanceDiagnostic(
            "UI serial={Serial} talkNode8Visible={TalkNode8Visible} talkNode9Visible={TalkNode9Visible} "
            + "agentPresent={AgentPresent} agentActive={AgentActive} agentReady={AgentReady} agentShown={AgentShown} "
            + "talkAutoMessageSettingAddonId={TalkAutoMessageSettingAddonId} "
            + "talkAutoMessageSelectorAddonId={TalkAutoMessageSelectorAddonId} "
            + "talkAutoMessageSelectorCancelAddonId={TalkAutoMessageSelectorCancelAddonId} "
            + "pendingTextAutoAdvanceScope={PendingTextAutoAdvanceScope} "
            + "pendingUnvoicedAutoAdvanceSpeed={PendingUnvoicedAutoAdvanceSpeed} "
            + "talkAutoMessageSettingPresent={TalkAutoMessageSettingPresent} "
            + "talkAutoMessageSettingVisible={TalkAutoMessageSettingVisible} "
            + "talkAutoMessageSelectorPresent={TalkAutoMessageSelectorPresent} "
            + "talkAutoMessageSelectorVisible={TalkAutoMessageSelectorVisible} "
            + "talkAutoMessageSelectorCancelPresent={TalkAutoMessageSelectorCancelPresent} "
            + "talkAutoMessageSelectorCancelVisible={TalkAutoMessageSelectorCancelVisible} decision=observed",
            snapshot.TalkSerial?.ToString() ?? "none",
            snapshot.TalkNode8Visible?.ToString() ?? "absent",
            snapshot.TalkNode9Visible?.ToString() ?? "absent",
            snapshot.AgentPresent?.ToString() ?? "absent",
            snapshot.AgentActive?.ToString() ?? "absent",
            snapshot.AgentReady?.ToString() ?? "absent",
            snapshot.AgentShown?.ToString() ?? "absent",
            snapshot.TalkAutoMessageSettingAddonId?.ToString() ?? "absent",
            snapshot.TalkAutoMessageSelectorAddonId?.ToString() ?? "absent",
            snapshot.TalkAutoMessageSelectorCancelAddonId?.ToString() ?? "absent",
            snapshot.PendingTextAutoAdvanceSetting?.ToString() ?? "absent",
            snapshot.PendingUnvoicedAutoAdvanceSpeed?.ToString() ?? "absent",
            snapshot.TalkAutoMessageSettingPresent?.ToString() ?? "absent",
            snapshot.TalkAutoMessageSettingVisible?.ToString() ?? "absent",
            snapshot.TalkAutoMessageSelectorPresent?.ToString() ?? "absent",
            snapshot.TalkAutoMessageSelectorVisible?.ToString() ?? "absent",
            snapshot.TalkAutoMessageSelectorCancelPresent?.ToString() ?? "absent",
            snapshot.TalkAutoMessageSelectorCancelVisible?.ToString() ?? "absent");
    }

    private void OnLineBuffered(DubLine line)
    {
        if (String.Equals(line.SourceQuest, PreDubLineSource, StringComparison.Ordinal)) return;
        if (line.ActualStatus == ActualStatus.Predicted)
        {
            if (gameMixerBackend is { } mixer)
            {
                var gain = configuration.Volume;
                _ = ObservePreparedPredictionAsync(line, mixer, gain);
            }
            else
            {
                TryCompleteAutoAdvance();
            }
            return;
        }
        var current = talk.Current;
        if (current is null || session?.Epoch != line.SessionEpoch || line.ActualStatus != ActualStatus.Actual) return;
        // Playback authority: actual Talk remains visible and exact text still matches.
        if (current.Text != line.Text || current.Speaker != line.SpeakerName
            || line.ActualTalkSerial is not { } serial
            || !talk.IsPresentationReady(serial)) return;
        audio?.Play(line, configuration.Volume);
    }

    private void OnTalkPresentationReady(ActualTalkLine talkLine) => QueueFrameworkAction(() =>
    {
        var current = session;
        if (current is null || talk.Current?.Serial != talkLine.Serial) return;
        var line = current.Lines.FirstOrDefault(value =>
            value.ActualStatus == ActualStatus.Actual
            && value.ActualTalkSerial == talkLine.Serial
            && value.State == DubLineState.Buffered);
        if (line is not null) OnLineBuffered(line);
    }, "Talk presentation-ready framework dispatch failed");

    private async Task ObservePreparedPredictionAsync(
        DubLine line, IGameMixerAudioBackend mixer, float volume)
    {
        try
        {
            await mixer.PrepareAsync(line, volume, line.Token).ConfigureAwait(false);
            QueueFrameworkAction(TryCompleteAutoAdvance,
                "Prepared-prediction auto-advance dispatch failed");
        }
        catch (OperationCanceledException) when (line.Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            log.Warning(error, "Game-mixer preparation failed for predicted line {Sequence}", line.Sequence);
            line.Cancel(DubLineState.Failed);
            QueueFrameworkAction(TryCompleteAutoAdvance,
                "Failed-prediction auto-advance dispatch failed");
        }
    }

    private void OnAudioFinished(DubLine line)
    {
        if (!configuration.AutoAdvanceDubbedCutsceneDialogue
            || line.SourceQuest == DebugLineSource
            || line.ActualStatus != ActualStatus.Actual
            || line.State != DubLineState.Completed
            || line.NativeVoiceStatus == NativeVoiceStatus.NativeVoiced
            || line.ActualTalkSerial is not { } serial
            || !cutscenes.IsInCutscene
            || session is not { } current
            || talk.Current?.Serial != serial
            || ShouldPreserveGameControlledPacing(serial)) return;
        Interlocked.Exchange(ref pendingAutoAdvance,
            new(current.Epoch, line.Sequence, serial, line.SpeakerName, line.Text,
                line.NextPredictionKeys));
        TryCompleteAutoAdvance();
    }

    private void OnAudioFailed(DubLine line, Exception error)
    {
        log.Warning(error, "Selected audio backend failed for line {Sequence}; backend remains scene-locked",
            line.Sequence);
        if (line.ActualStatus == ActualStatus.Actual && session?.Epoch == line.SessionEpoch)
            Volatile.Write(ref suppressAutomaticAdvance, 0);
        IsSpeaking = false;
    }

    private void TryCompleteAutoAdvance()
    {
        var pending = Volatile.Read(ref pendingAutoAdvance);
        var current = session;
        if (pending is null) return;
        if (current is null || current.Epoch != pending.SessionEpoch
            || current.CancellationToken.IsCancellationRequested
            || !configuration.AutoAdvanceDubbedCutsceneDialogue)
        {
            Interlocked.CompareExchange(ref pendingAutoAdvance, null, pending);
            CancelAutoAdvanceRetry();
            return;
        }
        if (ShouldPreserveGameControlledPacing(pending.TalkSerial))
        {
            Interlocked.CompareExchange(ref pendingAutoAdvance, null, pending);
            CancelAutoAdvanceRetry();
            return;
        }
        var successorState = AutoAdvancePolicy.GetSuccessorSetState(
            current.Lines, pending.NextPredictionKeys,
            true);
        if (successorState == AutoAdvanceSuccessorState.Waiting)
        {
            // The next prediction may still be resolving.  Keep one bounded
            // retry task alive so readiness events are not the sole progress
            // path; the task exits on Talk/session invalidation or disposal.
            ScheduleAutoAdvanceRetry(pending);
            return;
        }
        if (successorState == AutoAdvanceSuccessorState.Unavailable)
            log.Warning("Auto-advance successor {PredictionKey} is unavailable; advancing after completed playback",
                pending.NextPredictionKeys.Count == 0
                    ? "unknown"
                    : String.Join(',', pending.NextPredictionKeys));
        // Framework dispatch can fail transiently while Talk is redrawing.
        // Keep the request until the exact guarded advance succeeds; the
        // dispatch gate prevents repeated queued attempts from spinning.
        if (Interlocked.CompareExchange(ref autoAdvanceDispatching, 1, 0) != 0) return;
        try
        {
            var dispatch = framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    if (configuration.AutoAdvanceDubbedCutsceneDialogue && cutscenes.IsInCutscene
                        && ReferenceEquals(Volatile.Read(ref pendingAutoAdvance), pending))
                    {
                        if (talk.TryAdvance(pending.TalkSerial, pending.Speaker, pending.Text))
                        {
                            CompleteAutoAdvance(pending);
                        }
                        else
                        {
                            ScheduleAutoAdvanceRetry(pending);
                        }
                    }
                }
                finally { Volatile.Write(ref autoAdvanceDispatching, 0); }
            });
            _ = dispatch.ContinueWith(task =>
            {
                if (task.IsFaulted) _ = task.Exception;
                Volatile.Write(ref autoAdvanceDispatching, 0);
                if (task.IsFaulted || task.IsCanceled) ScheduleAutoAdvanceRetry(pending);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch
        {
            Volatile.Write(ref autoAdvanceDispatching, 0);
            ScheduleAutoAdvanceRetry(pending);
        }
    }

    private void ScheduleAutoAdvanceRetry(PendingAutoAdvance pending)
    {
        lock (autoAdvanceRetryGate)
        {
            if (Volatile.Read(ref disposed) != 0 || !IsCurrentAutoAdvance(pending)
                || autoAdvanceRetryTask is not null) return;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                officialObservationShutdown.Token);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var generation = ++autoAdvanceRetryGeneration;
            activeAutoAdvanceRetryGeneration = generation;
            autoAdvanceRetryCancellation = cancellation;
            autoAdvanceRetryTask = completion.Task;
            _ = RetryAutoAdvanceAsync(pending, cancellation, completion, generation);
        }
    }

    private async Task RetryAutoAdvanceAsync(PendingAutoAdvance pending, CancellationTokenSource cancellation,
        TaskCompletionSource completion, long generation)
    {
        var retryAfterFailure = false;
        var delay = TimeSpan.FromMilliseconds(100);
        try
        {
            while (!cancellation.IsCancellationRequested && IsCurrentAutoAdvance(pending))
            {
                await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
                if (!cancellation.IsCancellationRequested && IsCurrentAutoAdvance(pending))
                    QueueFrameworkAction(TryCompleteAutoAdvance,
                        "Auto-advance retry framework dispatch failed");
                delay = TimeSpan.FromMilliseconds(Math.Min(1000, delay.TotalMilliseconds * 2));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            retryAfterFailure = true;
            log.Warning(error, "Auto-advance retry failed");
        }
        finally
        {
            var retry = false;
            lock (autoAdvanceRetryGate)
            {
                completion.TrySetResult();
                if (activeAutoAdvanceRetryGeneration == generation
                    && ReferenceEquals(autoAdvanceRetryCancellation, cancellation))
                {
                    // The generation check prevents an old canceled finalizer
                    // from clearing a newly published retry state.
                    autoAdvanceRetryCancellation = null;
                    autoAdvanceRetryTask = null;
                    activeAutoAdvanceRetryGeneration = null;
                    retry = retryAfterFailure && Volatile.Read(ref disposed) == 0
                        && IsCurrentAutoAdvance(pending);
                }
            }
            cancellation.Dispose();
            if (retry) ScheduleAutoAdvanceRetry(pending);
        }
    }

    private bool IsCurrentAutoAdvance(PendingAutoAdvance pending)
    {
        var current = session;
        return Volatile.Read(ref disposed) == 0
            && configuration.AutoAdvanceDubbedCutsceneDialogue
            && current is not null && current.Epoch == pending.SessionEpoch
            && !current.CancellationToken.IsCancellationRequested;
    }

    private void CancelAutoAdvanceRetry()
    {
        CancellationTokenSource? cancellation;
        lock (autoAdvanceRetryGate)
        {
            // Detach first. A replacement retry may publish immediately; the
            // old task's finally block can no longer clear that state.
            cancellation = autoAdvanceRetryCancellation;
            autoAdvanceRetryCancellation = null;
            autoAdvanceRetryTask = null;
            activeAutoAdvanceRetryGeneration = null;
            autoAdvanceRetryGeneration++;
        }
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void CompleteAutoAdvance(PendingAutoAdvance pending)
    {
        CancellationTokenSource? cancellation;
        lock (autoAdvanceRetryGate)
        {
            Interlocked.CompareExchange(ref pendingAutoAdvance, null, pending);
            cancellation = autoAdvanceRetryCancellation;
            autoAdvanceRetryCancellation = null;
            autoAdvanceRetryTask = null;
            activeAutoAdvanceRetryGeneration = null;
            autoAdvanceRetryGeneration++;
        }
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task StopAutoAdvanceRetryAsync()
    {
        Task? retry;
        CancellationTokenSource? cancellation;
        lock (autoAdvanceRetryGate)
        {
            cancellation = autoAdvanceRetryCancellation;
            retry = autoAdvanceRetryTask;
            autoAdvanceRetryCancellation = null;
            autoAdvanceRetryTask = null;
            activeAutoAdvanceRetryGeneration = null;
            autoAdvanceRetryGeneration++;
        }
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (retry is null) return;
        try { await retry.ConfigureAwait(false); }
        catch (Exception error) { log.Warning(error, "Auto-advance retry task failed during shutdown"); }
    }

    private void OnTalkClosed(ActualTalkLine? _) => QueueFrameworkAction(
        OnTalkClosedOnFramework, "Talk-close framework dispatch failed");

    private void OnTalkClosedOnFramework()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        autoAdvanceDiagnostics.Reset();
        Interlocked.Exchange(ref pendingAutoAdvance, null);
        CancelAutoAdvanceRetry();
        Volatile.Write(ref autoAdvanceDispatching, 0);
        Volatile.Write(ref suppressAutomaticAdvance, 0);
        Volatile.Write(ref talkAdvanceContext, null);
        Volatile.Write(ref gameControlledAdvanceSerial, 0);
        audio?.Stop();
        lipSync.Stop();
        CancelActualLines();
        ScheduleTalkIdleCleanup();
    }

    private void ScheduleTalkIdleCleanup()
    {
        var generation = Interlocked.Increment(ref talkIdleGeneration);
        _ = CleanupAfterTalkIdleAsync(generation, officialObservationShutdown.Token);
    }

    private async Task CleanupAfterTalkIdleAsync(long generation, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), token).ConfigureAwait(false);
            QueueFrameworkAction(() =>
            {
                if (generation != Volatile.Read(ref talkIdleGeneration) || talk.Current is not null) return;
                if (!cutscenes.IsInCutscene)
                {
                    CancelSession();
                    return;
                }
                if (!configuration.BackgroundCasting)
                    session?.ReconcilePredictions([], preserveExisting: false);
            }, "Talk-idle speculative cleanup dispatch failed");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void CancelActualLines()
    {
        if (session is not { } current) return;
        foreach (var line in current.Lines.Where(line => line.ActualStatus == ActualStatus.Actual && !line.IsTerminal))
            line.Cancel(DubLineState.Invalidated);
    }

    private void QueueFrameworkAction(Action action, string failureMessage)
    {
        if (Volatile.Read(ref disposed) != 0 || framework.IsFrameworkUnloading) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (coordinatorTaskGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            coordinatorTasks.Add(completion.Task);
            frameworkDispatchTasks.Add(completion.Task);
        }
        try
        {
            var dispatch = framework.RunOnFrameworkThread(() =>
            {
                Volatile.Write(ref frameworkThreadId, Environment.CurrentManagedThreadId);
                var previous = activeFrameworkDispatch.Value;
                activeFrameworkDispatch.Value = completion.Task;
                try
                {
                    if (Volatile.Read(ref disposed) == 0 && !framework.IsFrameworkUnloading) action();
                    completion.TrySetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { activeFrameworkDispatch.Value = previous; }
            });
            _ = ObserveFrameworkDispatchAsync(dispatch, completion, failureMessage);
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
            _ = ObserveFrameworkDispatchAsync(Task.CompletedTask, completion, failureMessage);
        }
    }

    private async Task ObserveFrameworkDispatchAsync(
        Task dispatch, TaskCompletionSource completion, string failureMessage)
    {
        Exception? failure = null;
        try
        {
            try
            {
                await dispatch.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled();
            }
            catch (Exception error)
            {
                failure = error;
                completion.TrySetException(error);
            }
            try { await completion.Task.ConfigureAwait(false); }
            catch (Exception error)
            {
                failure ??= error;
            }
            if (failure is not null && failure is not OperationCanceledException)
                log.Warning(failure is AggregateException aggregate ? aggregate.GetBaseException() : failure,
                    failureMessage);
        }
        finally
        {
            completion.TrySetResult();
            lock (coordinatorTaskGate)
            {
                coordinatorTasks.Remove(completion.Task);
                frameworkDispatchTasks.Remove(completion.Task);
            }
        }
    }

    private void QueueProfileUpgradeNotification(string stableKey, string profileId) => QueueFrameworkAction(
        () => SpeakerProfileUpgraded?.Invoke(stableKey, profileId),
        "Speaker-profile IPC notification dispatch failed");

    private Task DispatchFrameworkActionAsync(Action action, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            token, officialObservationShutdown.Token);
        if (Volatile.Read(ref disposed) != 0 || framework.IsFrameworkUnloading)
        {
            completion.TrySetCanceled();
            return AwaitFrameworkCompletionAsync(completion.Task, linked);
        }
        lock (coordinatorTaskGate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                completion.TrySetCanceled();
                return AwaitFrameworkCompletionAsync(completion.Task, linked);
            }
            coordinatorTasks.Add(completion.Task);
            frameworkDispatchTasks.Add(completion.Task);
        }
        try
        {
            var dispatch = framework.RunOnFrameworkThread(() =>
            {
                Volatile.Write(ref frameworkThreadId, Environment.CurrentManagedThreadId);
                try
                {
                    if (Volatile.Read(ref disposed) != 0
                        || framework.IsFrameworkUnloading)
                    {
                        completion.TrySetCanceled();
                        return;
                    }
                    action();
                    completion.TrySetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
            });
            _ = ObserveFrameworkDispatchAsync(dispatch, completion,
                "Framework action dispatch failed");
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
            _ = ObserveFrameworkCompletionAsync(Task.CompletedTask, completion.Task,
                "Framework action dispatch failed");
        }
        return AwaitFrameworkCompletionAsync(completion.Task, linked);
    }

    private async Task AwaitFrameworkCompletionAsync(Task completion,
        CancellationTokenSource linked)
    {
        try { await completion.WaitAsync(linked.Token).ConfigureAwait(false); }
        finally
        {
            linked.Dispose();
            lock (coordinatorTaskGate)
            {
                coordinatorTasks.Remove(completion);
                frameworkDispatchTasks.Remove(completion);
            }
        }
    }

    private static ResolvedSpeaker CanonicalizeOfficialAlias(
        ResolvedSpeaker resolved, OfficialVoiceGroup group) => resolved with
    {
        StableKey = OfficialVoiceCatalog.CanonicalSpeakerKey(group.Id),
        NpcBaseId = null,
        DisplayName = group.Label,
        SceneLocal = false,
        Evidence = resolved.Evidence with
        {
            StableSpeakerKey = OfficialVoiceCatalog.CanonicalSpeakerKey(group.Id),
            NpcBaseId = null,
            ModifierIds = resolved.Evidence.ModifierIds?
                .Where(value => !String.Equals(value, "scene_local", StringComparison.Ordinal))
                .ToArray(),
        },
        Metadata = resolved.Metadata with { EvidenceSource = "official-catalog" },
    };

    private static ResolvedSpeaker SnapshotResolvedSpeaker(ResolvedSpeaker resolved) => resolved with
    {
        Evidence = resolved.Evidence with
        {
            ModifierIds = resolved.Evidence.ModifierIds?.ToArray(),
        },
    };

    private static bool SameActorEvidence(ResolvedSpeaker expected, ResolvedSpeaker observed) =>
        expected.ActorAddress == observed.ActorAddress && expected.NpcBaseId == observed.NpcBaseId;

    private void ScheduleDebugBaseVoiceRefresh(string language)
    {
        TaskCompletionSource? completion = null;
        lock (debugRefreshGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            debugRefreshLanguage = language;
            if (debugRefreshTask is null)
            {
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                debugRefreshTask = completion.Task;
            }
        }
        if (completion is not null) _ = RunDebugBaseVoiceRefreshAsync(completion);
    }

    private async Task RunDebugBaseVoiceRefreshAsync(TaskCompletionSource completion)
    {
        try
        {
            while (!officialObservationShutdown.IsCancellationRequested)
            {
                string? language;
                lock (debugRefreshGate)
                {
                    language = debugRefreshLanguage;
                    debugRefreshLanguage = null;
                }
                if (language is null) return;
                try
                {
                    await RefreshDebugBaseVoicesAsync(language, officialObservationShutdown.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (officialObservationShutdown.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    log.Warning(error, "Debug Base-voice refresh failed; retaining queued refresh");
                    lock (debugRefreshGate) debugRefreshLanguage = language;
                    await Task.Delay(TimeSpan.FromSeconds(1), officialObservationShutdown.Token)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (officialObservationShutdown.IsCancellationRequested) { }
        catch (Exception error) { log.Warning(error, "Debug Base-voice refresh coordinator failed"); }
        finally
        {
            var retryLanguage = (string?)null;
            lock (debugRefreshGate)
            {
                if (ReferenceEquals(debugRefreshTask, completion.Task)) debugRefreshTask = null;
                if (!officialObservationShutdown.IsCancellationRequested)
                    retryLanguage = debugRefreshLanguage;
                completion.TrySetResult();
            }
            if (retryLanguage is not null) ScheduleDebugBaseVoiceRefresh(retryLanguage);
        }
    }

    private async Task WaitForDebugBaseVoiceRefreshShutdownAsync()
    {
        Task? refresh;
        lock (debugRefreshGate) refresh = debugRefreshTask;
        if (refresh is null) return;
        try { await refresh.ConfigureAwait(false); }
        catch (Exception error) { log.Warning(error, "Debug Base-voice refresh task failed during shutdown"); }
    }

    private bool IsCurrent(CutsceneSession candidate) =>
        Volatile.Read(ref disposed) == 0 && ReferenceEquals(session, candidate)
        && !candidate.CancellationToken.IsCancellationRequested;

    private void CancelSession(bool avoidFrameworkDispatch = false)
    {
        Interlocked.Increment(ref talkIdleGeneration);
        autoAdvanceDiagnostics.Reset();
        Interlocked.Exchange(ref pendingAutoAdvance, null);
        CancelAutoAdvanceRetry();
        Volatile.Write(ref autoAdvanceDispatching, 0);
        Volatile.Write(ref talkAdvanceContext, null);
        Volatile.Write(ref gameControlledAdvanceSerial, 0);
        audio?.Stop();
        lipSync.Stop(avoidFrameworkDispatch);
        IsSpeaking = false;
        if (session is { } current) scheduler?.InvalidateEpoch(current.Epoch);
        session?.Dispose();
        session = null;
        prefetcher.EndSession();
        cutsceneSpeakerKeys.Clear();
        cutsceneSpeakerAssignments.Clear();
        ReleaseCutsceneBaseResidency();
    }

    private void EnsureCutsceneBaseResidency()
    {
        var manager = runtimeManager
            ?? throw new InvalidOperationException("Base runtime is unavailable");
        if (Interlocked.CompareExchange(ref cutsceneBaseResidencyHeld, 1, 0) == 0)
            manager.AcquireBaseResidencyLease();
    }

    private void ReleaseCutsceneBaseResidency()
    {
        if (Interlocked.Exchange(ref cutsceneBaseResidencyHeld, 0) == 0) return;
        var manager = runtimeManager;
        if (manager is null) return;
        Task release;
        try { release = manager.ReleaseBaseResidencyLeaseAsync(CancellationToken.None); }
        catch (Exception error)
        {
            log.Warning(error, "Base cutscene residency release could not be scheduled");
            return;
        }
        lock (coordinatorTaskGate) coordinatorTasks.Add(release);
        _ = ObserveBaseResidencyReleaseAsync(release);
    }

    private async Task ObserveBaseResidencyReleaseAsync(Task release)
    {
        try { await release.ConfigureAwait(false); }
        catch (Exception error) { log.Warning(error, "Base cutscene residency release failed"); }
        finally
        {
            lock (coordinatorTaskGate) coordinatorTasks.Remove(release);
        }
    }

    public Task RegenerateCurrentTerritoryVoicesAsync(CancellationToken token) =>
        RunCoordinatorExclusiveAsync(token, async operationToken =>
        {
            var pool = domainPool
                ?? throw new InvalidOperationException("VoiceDesign and the casting-domain pool are not ready");
            await pool.RegenerateCurrentTerritoryAsync(operationToken).ConfigureAwait(false);
        });

    public Task RegenerateCurrentZoneVoicesAsync(CancellationToken token) =>
        RegenerateCurrentTerritoryVoicesAsync(token);

    public Task RegenerateDomainVoicesAsync(string domainId, CancellationToken token) =>
        RunCoordinatorExclusiveAsync(token, async operationToken =>
        {
            var pool = domainPool
                ?? throw new InvalidOperationException("VoiceDesign and the casting-domain pool are not ready");
            await pool.RegenerateDomainAsync(domainId, operationToken).ConfigureAwait(false);
        });

    public Task SetBackendAsync(BackendInfo backend, CancellationToken token) =>
        RunCoordinatorExclusiveAsync(token, async operationToken =>
        {
            var manager = runtimeManager
                ?? throw new InvalidOperationException("The inference runtime is not ready");
            await manager.SetDesiredAsync(backend, operationToken).ConfigureAwait(false);
        });

    public Task RebuildBackendBenchmarkAsync(CancellationToken token) =>
        RunCoordinatorExclusiveAsync(token, async operationToken =>
        {
            var manager = runtimeManager
                ?? throw new InvalidOperationException("The inference runtime is not ready");
            var designPath = bootstrap.VoiceDesignPath
                ?? throw new InvalidOperationException("VoiceDesign is not ready");
            await manager.BenchmarkAndApplyAsync(designPath, operationToken).ConfigureAwait(false);
        });

    private Task RunCoordinatorExclusiveAsync(
        CancellationToken token, Func<CancellationToken, Task> operation)
    {
        if (Volatile.Read(ref disposed) != 0)
            return Task.FromException(new ObjectDisposedException(nameof(SessionCoordinator)));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (coordinatorTaskGate)
        {
            if (Volatile.Read(ref disposed) != 0)
                return Task.FromException(new ObjectDisposedException(nameof(SessionCoordinator)));
            coordinatorTasks.Add(completion.Task);
        }
        _ = RunCoordinatorExclusiveCoreAsync(token, operation, completion);
        return completion.Task;
    }

    private async Task RunCoordinatorExclusiveCoreAsync(
        CancellationToken token, Func<CancellationToken, Task> operation,
        TaskCompletionSource completion)
    {
        CancellationTokenSource? linked = null;
        var entered = false;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                token, officialObservationShutdown.Token);
            await debugGate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            Volatile.Write(ref exclusiveOperationActive, 1);
            CancelPreDubOnFramework();
            InvalidateBaseHotLoadSafetyOnFramework();
            domainPool?.Pause();
            if (domainPool is { } pool)
                await pool.WaitForIdleAsync(linked.Token).ConfigureAwait(false);
            await operation(linked.Token).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException error) when (linked?.IsCancellationRequested == true)
        {
            completion.TrySetCanceled(error.CancellationToken);
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
        finally
        {
            if (entered)
            {
                try
                {
                    if (CanRestoreAfterExclusiveOperation())
                    {
                        var state = await CaptureFrameworkStateAsync(officialObservationShutdown.Token)
                            .ConfigureAwait(false);
                        if (CanRestoreAfterExclusiveOperation())
                            domainPool?.ActivateTerritory(state.TerritoryPlaceName);
                    }
                }
                catch (Exception) when (!CanRestoreAfterExclusiveOperation()) { }
                catch (Exception error)
                {
                    log.Warning(error, "Failed to restore casting-domain activation after exclusive operation");
                }
                Volatile.Write(ref exclusiveOperationActive, 0);
                if (CanRestoreAfterExclusiveOperation())
                {
                    try
                    {
                        await CaptureFrameworkStateAsync(officialObservationShutdown.Token).ConfigureAwait(false);
                    }
                    catch (Exception) when (!CanRestoreAfterExclusiveOperation()) { }
                    catch (Exception error)
                    {
                        log.Warning(error, "Failed to recompute Base hot-load safety after exclusive operation");
                    }
                }
                if (CanRestoreAfterExclusiveOperation())
                {
                    RequestBaseHotLoadRestore();
                    QueueFrameworkAction(SchedulePreDubOnFramework,
                        "Pre-dub restore after exclusive operation failed");
                }
                debugGate.Release();
            }
            lock (coordinatorTaskGate) coordinatorTasks.Remove(completion.Task);
            linked?.Dispose();
        }
    }

    private bool CanRestoreAfterExclusiveOperation() =>
        Volatile.Read(ref disposed) == 0
        && !officialObservationShutdown.IsCancellationRequested
        && !framework.IsFrameworkUnloading;

    public CastingPoolSnapshot? GetCastingPoolSnapshot() => domainPool?.Snapshot;

    public DebugInferenceSnapshot GetDebugInferenceSnapshot(string language)
    {
        var normalizedLanguage = NormalizeLanguage(language);
        var manager = runtimeManager;
        var designer = voiceDesigner;
        var selected = manager?.Selection?.Effective;
        var normalPlayback = IsSpeaking && Volatile.Read(ref debugPlaybackActive) == 0;
        var ready = bootstrap.State == BootstrapState.Ready
                    && manager is not null && selected is not null && voiceDesignPath is not null && audio is not null
                    && manager.IsReady && (designer is null || designer.IsReady)
                    && Volatile.Read(ref exclusiveOperationActive) == 0
                    && !normalPlayback;
        var readiness = cutscenes.IsInCutscene
            ? "Unavailable during a cutscene"
            : condition[ConditionFlag.InCombat]
                ? "Unavailable during combat"
                : normalPlayback
                    ? "Unavailable while normal Resonance playback is active"
                    : Volatile.Read(ref exclusiveOperationActive) != 0
                        ? "Unavailable while backend or casting maintenance is active"
                        : ready
                        ? designer is null
                            ? "Ready; VoiceDesign loads on first use"
                            : "Base and VoiceDesign initialized"
                        : "Waiting for model files, runtime, and audio initialization";
        var cachedLanguage = Volatile.Read(ref debugVoiceLanguage);
        if (!String.Equals(cachedLanguage, normalizedLanguage, StringComparison.Ordinal))
            readiness += "; refresh Base voices for this language";
        return new(
            ready && !cutscenes.IsInCutscene && !condition[ConditionFlag.InCombat],
            Volatile.Read(ref debugRunning) != 0 || Volatile.Read(ref debugPlaybackActive) != 0,
            readiness,
            selected is null ? "Preparing..." : $"{selected.Description} ({selected.Type})",
            designer?.BackendName ?? "not loaded",
            Volatile.Read(ref debugStatus),
            String.Equals(cachedLanguage, normalizedLanguage, StringComparison.Ordinal)
                ? Volatile.Read(ref debugBaseVoices)
                : officialVoiceCatalog.Groups.Select(value =>
                    new DebugBaseVoiceOption(value.Id, value.Label, false)).ToArray());
    }

    private Task<DebugInferenceSnapshot> CaptureDebugInferenceSnapshotAsync(
        string language, CancellationToken token)
    {
        if (framework.IsFrameworkUnloading)
            return Task.FromCanceled<DebugInferenceSnapshot>(new CancellationToken(true));
        var completion = new TaskCompletionSource<DebugInferenceSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var dispatch = framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    if (Volatile.Read(ref disposed) != 0
                        || framework.IsFrameworkUnloading)
                    {
                        completion.TrySetCanceled();
                        return;
                    }
                    completion.TrySetResult(GetDebugInferenceSnapshot(language));
                }
                catch (Exception error) { completion.TrySetException(error); }
            });
            _ = ObserveFrameworkCompletionAsync(dispatch, completion,
                "Debug inference snapshot dispatch failed");
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
            _ = ObserveFrameworkCompletionAsync(Task.CompletedTask, completion.Task,
                "Debug inference snapshot dispatch failed");
        }
        return completion.Task.WaitAsync(token);
    }

    public async Task RefreshDebugBaseVoicesAsync(string language, CancellationToken token)
    {
        var normalizedLanguage = NormalizeLanguage(language);
        var options = new List<DebugBaseVoiceOption>(officialVoiceCatalog.Groups.Count);
        debugBaseProfiles.Clear();
        foreach (var group in officialVoiceCatalog.Groups)
        {
            var profile = await voices.GetBestVoiceByStableKeyAsync(
                OfficialVoiceCatalog.CanonicalSpeakerKey(group.Id), normalizedLanguage,
                (runtimeManager ?? throw new InvalidOperationException("Base runtime is unavailable")).ModelHash,
                token).ConfigureAwait(false);
            if (profile is { Kind: VoiceProfileKind.Official })
                debugBaseProfiles[group.Id] = profile;
            var ready = debugBaseProfiles.ContainsKey(group.Id);
            options.Add(new(group.Id, group.Label, ready,
                ready ? "Ready" : "Not installed"));
        }
        var groupLabels = officialVoiceCatalog.Groups.Select(value => value.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var captured in await voices.GetOfficialVoiceProfilesAsync(normalizedLanguage, token).ConfigureAwait(false))
        {
            if (groupLabels.Contains(captured.DisplayName)) continue;
            var key = $"captured:{captured.Profile.Id}";
            debugBaseProfiles[key] = captured.Profile;
            options.Add(new(key, captured.DisplayName, true, "Ready — captured"));
        }
        Volatile.Write(ref debugBaseVoices, options);
        Volatile.Write(ref debugVoiceLanguage, normalizedLanguage);
    }

    private Task AttachShippedOfficialProfileAsync(
        long speakerId, ResolvedSpeaker speaker, OfficialVoiceGroup? resolvedGroup,
        string language, CancellationToken token)
    {
        var group = resolvedGroup
                    ?? officialVoiceCatalog.Resolve(speaker.NpcBaseId, speaker.DisplayName, language);
        if (group is null) return Task.CompletedTask;
        return PrepareAndAttachAsync(group);

        async Task PrepareAndAttachAsync(OfficialVoiceGroup officialGroup)
        {
            // This lookup is intentionally awaited before line synthesis.  It
            // is cheap and lets an already-built same-language official clone
            // win the first line without racing a new design assignment.
            var profile = await GetShippedOfficialProfileAsync(officialGroup, language, token)
                .ConfigureAwait(false);
            if (profile is not null)
                await AttachOfficialProfileAsync(speakerId, profile, language, token).ConfigureAwait(false);
        }
    }

    private async Task AttachOfficialProfileAsync(
        long speakerId, StoredVoiceProfile profile, string language, CancellationToken token)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            token, officialObservationShutdown.Token);
        profile = await voices.SaveAndAssignAsync(speakerId, profile, linked.Token)
            .ConfigureAwait(false);
        if (speakerKeys.TryGetValue(speakerId, out var stableKey))
        {
            profileCache[ProfileCacheKey(stableKey, language, profile.ModelHash)] = profile;
            QueueProfileUpgradeNotification(stableKey, profile.Id);
        }
    }

    private async Task<StoredVoiceProfile?> GetShippedOfficialProfileAsync(
        OfficialVoiceGroup group,
        string language,
        CancellationToken token)
    {
        language = NormalizeLanguage(language);
        var canonicalKey = OfficialVoiceCatalog.CanonicalSpeakerKey(group.Id);
        var currentModelHash = (runtimeManager
            ?? throw new InvalidOperationException("Base runtime is unavailable")).ModelHash;
        var current = await voices.GetBestVoiceByStableKeyAsync(
            canonicalKey, language, currentModelHash, token).ConfigureAwait(false);
        return current is { Kind: VoiceProfileKind.Official } ? current : null;
    }

    public Task RunVoiceDesignDebugAsync(
        string text,
        string instruction,
        string language,
        CancellationToken token) => RunDebugAsync("VoiceDesign", text, language, false, token, async (line, activeToken) =>
    {
        var designer = await EnsureVoiceDesignerAsync(activeToken).ConfigureAwait(false);
        var backend = runtimeManager?.Selection?.Effective.Name
                      ?? throw new InvalidOperationException("No inference device is selected");
        await designer.SwitchBackendAsync(backend, activeToken).ConfigureAwait(false);
        await designer.SynthesizeDesignedLineOnlyAsync(
            line.Text, instruction, 0x5245534f4e414e43L, line.Language!, line.Audio, activeToken).ConfigureAwait(false);
    });

    public async Task RunBaseDebugAsync(
        string presetKey,
        string text,
        string language,
        CancellationToken token)
    {
        var normalizedLanguage = NormalizeLanguage(language);
        if (!String.Equals(Volatile.Read(ref debugVoiceLanguage), normalizedLanguage, StringComparison.Ordinal))
            await RefreshDebugBaseVoicesAsync(normalizedLanguage, token).ConfigureAwait(false);
        await RunDebugAsync($"Base-{presetKey}", text, normalizedLanguage, true, token, async (line, activeToken) =>
        {
            if (!debugBaseProfiles.TryGetValue(presetKey, out var profile))
            {
                var group = officialVoiceCatalog.Groups.SingleOrDefault(value => value.Id == presetKey)
                            ?? throw new InvalidOperationException("The selected official voice group does not exist");
                // Debug inference must never start curated extraction while
                // the debug gate owns the selected device.  Normal dialogue
                // queues that work for safe idle; debug consumes only a
                // profile already attached to the selected language.
                profile = await GetShippedOfficialProfileAsync(
                              group, normalizedLanguage, activeToken)
                              .ConfigureAwait(false)
                          ?? throw new InvalidOperationException(
                              "The selected character's official profile pack is not installed");
                debugBaseProfiles[presetKey] = profile;
                await RefreshDebugBaseVoicesAsync(normalizedLanguage, activeToken).ConfigureAwait(false);
            }
            var manager = runtimeManager ?? throw new InvalidOperationException("Base runtime is not initialized");
            await manager.SynthesizeAsync(
                new(line.Text, line.Language!, profile.Reference, null, 0x4241534554455354L),
                line.Audio,
                activeToken).ConfigureAwait(false);
            line.Audio.Complete();
        }).ConfigureAwait(false);
    }

    public void CancelDebugInference()
        => CancelDebugInference(false);

    private void CancelDebugInference(bool avoidFrameworkDispatch)
    {
        lock (debugCancellationGate) debugCancellation?.Cancel();
        if (avoidFrameworkDispatch || Volatile.Read(ref disposed) != 0)
            audio?.Stop();
        else
            QueueFrameworkAction(() => audio?.Stop(), "Debug audio cancellation dispatch failed");
        Volatile.Write(ref debugStatus, "Cancelled");
    }

    private void CompleteDebugPlayback(DubLine line, Exception? error = null)
    {
        TaskCompletionSource? completion;
        lock (debugCancellationGate)
            completion = ReferenceEquals(debugPlaybackLine, line) ? debugPlaybackCompletion : null;
        if (error is null) completion?.TrySetResult();
        else completion?.TrySetException(error);
    }

    private Task RunDebugAsync(
        string kind,
        string text,
        string language,
        bool applyBaseCloneCorrection,
        CancellationToken token,
        Func<DubLine, CancellationToken, Task> synthesize)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (debugCancellationGate)
        {
            if (Volatile.Read(ref disposed) != 0)
                return Task.FromException(new ObjectDisposedException(nameof(SessionCoordinator)));
            if (debugTask is not null)
                return Task.FromException(new InvalidOperationException("A debug inference or playback operation is already active"));
            debugTask = completion.Task;
        }
        return RunTrackedDebugAsync(kind, text, language, applyBaseCloneCorrection, token, synthesize, completion);
    }

    private async Task RunTrackedDebugAsync(
        string kind,
        string text,
        string language,
        bool applyBaseCloneCorrection,
        CancellationToken token,
        Func<DubLine, CancellationToken, Task> synthesize,
        TaskCompletionSource completion)
    {
        try
        {
            await RunDebugCoreAsync(kind, text, language, applyBaseCloneCorrection, token, synthesize).ConfigureAwait(false);
        }
        finally
        {
            completion.TrySetResult();
            lock (debugCancellationGate)
            {
                if (ReferenceEquals(debugTask, completion.Task)) debugTask = null;
            }
        }
    }

    private async Task RunDebugCoreAsync(
        string kind,
        string text,
        string language,
        bool applyBaseCloneCorrection,
        CancellationToken token,
        Func<DubLine, CancellationToken, Task> synthesize)
    {
        if (String.IsNullOrWhiteSpace(text)) throw new ArgumentException("Sample sentence is empty", nameof(text));
        using var coordinatorCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            token, officialObservationShutdown.Token);
        var operationToken = coordinatorCancellation.Token;
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(SessionCoordinator));
        var snapshot = await CaptureDebugInferenceSnapshotAsync(language, operationToken).ConfigureAwait(false);
        if (!snapshot.Ready) throw new InvalidOperationException(snapshot.Readiness);
        if (snapshot.Running) throw new InvalidOperationException("A debug inference or playback operation is already active");
        await debugGate.WaitAsync(operationToken).ConfigureAwait(false);
        CancellationTokenSource? cancellation = null;
        DubLine? line = null;
        try
        {
            // Publish exclusion before any asynchronous preparation drain. A
            // scheduler-idle callback that arrives during that drain must not
            // start a new official build beside the debug inference.
            Volatile.Write(ref debugRunning, 1);
            CancelPreDubOnFramework();
            InvalidateBaseHotLoadSafetyOnFramework();
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(SessionCoordinator));
            snapshot = await CaptureDebugInferenceSnapshotAsync(language, operationToken).ConfigureAwait(false);
            if (!snapshot.Ready) throw new InvalidOperationException(snapshot.Readiness);
            cancellation = coordinatorCancellation;
            lock (debugCancellationGate) debugCancellation = cancellation;
            domainPool?.Pause();
            if (domainPool is { } pool)
                await pool.WaitForIdleAsync(cancellation.Token).ConfigureAwait(false);
            Volatile.Write(ref debugStatus, $"{kind}: generating on {snapshot.Device}");
            line = new DubLine
            {
                SessionEpoch = 0,
                Sequence = Interlocked.Decrement(ref nextDebugSequence),
                SourceQuest = DebugLineSource,
                SpeakerKey = "debug",
                SpeakerName = kind,
                Text = text.Trim(),
                Language = NormalizeLanguage(language),
                ActualStatus = ActualStatus.Actual,
                NativeVoiceStatus = NativeVoiceStatus.NotVoiced,
                ApplyBaseCloneCorrection = applyBaseCloneCorrection,
                PlaybackDeadline = DateTimeOffset.MaxValue,
            };
            line.TryTransition(DubLineState.Generating, DubLineState.Predicted);
            StreamingAudioBuffer? debugCapture = configuration.ExportDebugBaseWav && applyBaseCloneCorrection
                ? line.Audio.CreateCapture()
                : null;
            audioBackendSession.SelectForDebug();
            var playbackCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (debugCancellationGate)
            {
                debugPlaybackLine = line;
                debugPlaybackCompletion = playbackCompletion;
            }
            await DispatchFrameworkActionAsync(() => audio!.Play(line, configuration.Volume),
                cancellation.Token).ConfigureAwait(false);
            await synthesize(line, cancellation.Token).ConfigureAwait(false);
            if (debugCapture is not null)
            {
                var captured = await debugCapture.DrainAsync(cancellation.Token).ConfigureAwait(false);
                var safeKind = String.Concat(kind.Select(value => Char.IsLetterOrDigit(value) || value is '-' or '_'
                    ? Char.ToLowerInvariant(value)
                    : '-')).Trim('-');
                var outputStem = Path.Combine(debugAudioDirectory,
                    $"{safeKind}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{line.Sequence}");
                var rawPath = outputStem + "-raw.wav";
                var correctedPath = outputStem + "-corrected.wav";
                DebugWavExporter.ExportRaw(rawPath, captured);
                DebugWavExporter.Export(correctedPath, captured, configuration.Volume);
                debugCapture.Dispose();
                Volatile.Write(ref debugStatus,
                    $"{kind}: raw/corrected WAVs saved to {debugAudioDirectory}; playback active");
            }
            else
            {
                Volatile.Write(ref debugStatus, $"{kind}: synthesis passed; playback active");
            }
            await playbackCompletion.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
            Volatile.Write(ref debugStatus, $"{kind}: playback passed");
            line = null; // AudioEngine owns disposal after playback.
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested || token.IsCancellationRequested)
        {
            try { await DispatchFrameworkActionAsync(() => audio?.Stop(), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception stopError) { log.Warning(stopError, "Debug audio cleanup dispatch failed"); }
            line?.Cancel();
            line?.Dispose();
            Volatile.Write(ref debugStatus, $"{kind}: cancelled");
            throw;
        }
        catch (Exception error)
        {
            try { await DispatchFrameworkActionAsync(() => audio?.Stop(), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception stopError) { log.Warning(stopError, "Debug audio cleanup dispatch failed"); }
            line?.Audio.Complete(error);
            line?.Cancel(DubLineState.Failed);
            line?.Dispose();
            Volatile.Write(ref debugStatus, $"{kind}: failed — {error.Message}");
            throw;
        }
        finally
        {
            lock (debugCancellationGate)
            {
                if (ReferenceEquals(debugCancellation, cancellation)) debugCancellation = null;
                if (ReferenceEquals(debugPlaybackLine, line) || line is null)
                {
                    debugPlaybackLine = null;
                    debugPlaybackCompletion = null;
                }
            }
            Volatile.Write(ref debugRunning, 0);
            Volatile.Write(ref debugPlaybackActive, 0);
            if (!audioBackendSession.IsSceneLocked)
            {
                audioBackendSession.EndDebug();
            }
            if (Volatile.Read(ref disposed) == 0
                && !officialObservationShutdown.IsCancellationRequested)
            {
                try
                {
                    var state = await CaptureFrameworkStateAsync(officialObservationShutdown.Token).ConfigureAwait(false);
                    domainPool?.ActivateTerritory(state.TerritoryPlaceName);
                }
                catch (Exception error) { log.Warning(error, "Failed to restore casting-domain activation after debug inference"); }
            }
            if (Volatile.Read(ref disposed) == 0)
            {
                RequestBaseHotLoadRestore();
                QueueFrameworkAction(SchedulePreDubOnFramework,
                    "Pre-dub restore after debug inference failed");
            }
            debugGate.Release();
        }
    }

    private static string NormalizeLanguage(string language) => language.Trim().ToLowerInvariant() switch
    {
        "en" or "eng" or "english" => "english",
        "ja" or "jpn" or "japanese" => "japanese",
        "de" or "deu" or "german" => "german",
        "fr" or "fra" or "french" => "french",
        _ => throw new NotSupportedException($"FFXIV dubbing language '{language}' is not supported"),
    };

    private bool IsFrameworkThread => framework.IsInFrameworkUpdateThread
        || activeFrameworkDispatch.Value is not null
        || Volatile.Read(ref frameworkThreadId) == Environment.CurrentManagedThreadId;

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposalTask is not null) return new ValueTask(disposalTask);
            var disposingFromFrameworkThread = IsFrameworkThread;
            // Disable callbacks before handing teardown to the worker.  The
            // worker must not be the first code to publish shutdown while a
            // framework callback can still enqueue more work.
            Interlocked.Exchange(ref disposed, 1);
            disposalTask = Task.Run(async () =>
                await DisposeCoreAsync(disposingFromFrameworkThread).ConfigureAwait(false));
            return new ValueTask(disposalTask);
        }
    }

    private async ValueTask DisposeCoreAsync(bool disposingFromFrameworkThread)
    {
        var failures = new List<Exception>();
        void Record(string resource, Exception error)
        {
            log.Warning(error, "{Resource} teardown failed", resource);
            failures.Add(new InvalidOperationException($"{resource} teardown failed", error));
        }

        async Task BestEffortAsync(string resource, Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch (Exception error) { Record(resource, error); }
        }

        async Task BestEffortFrameworkAsync(string resource, Func<Task> operation)
        {
            try
            {
                await operation().WaitAsync(TeardownFrameworkWait).ConfigureAwait(false);
            }
            catch (Exception error) { Record(resource, error); }
        }

        void BestEffortSync(string resource, Action operation)
        {
            try { operation(); }
            catch (Exception error) { Record(resource, error); }
        }

        BestEffortSync("Initial pre-dub cancellation", CancelPreDubOnFramework);
        BestEffortSync("Initial session cancellation", () => CancelSession(avoidFrameworkDispatch: true));
        BestEffortSync("Debug inference cancellation", () => CancelDebugInference(disposingFromFrameworkThread));
        try { officialObservationShutdown.Cancel(throwOnFirstException: false); }
        catch (Exception error) { Record("Coordinator cancellation", error); }
        Task? debugOperation;
        lock (debugCancellationGate) debugOperation = debugTask;
        if (debugOperation is not null)
        {
            var operation = debugOperation!;
            await BestEffortAsync("Debug inference", () => operation);
        }
        await BestEffortAsync("Debug gate drain", async () =>
        {
            await debugGate.WaitAsync(TeardownFrameworkWait).ConfigureAwait(false);
            debugGate.Release();
        });
        Task? backendSwitch;
        lock (backendSwitchGate) backendSwitch = backendSwitchTask;
        if (backendSwitch is not null)
        {
            var operation = backendSwitch!;
            await BestEffortAsync("Backend switch", () => operation);
        }
        cutscenes.Started -= OnCutsceneStarted;
        cutscenes.Ended -= OnCutsceneEnded;
        talk.LineChanged -= OnLineChanged;
        talk.PresentationReady -= OnTalkPresentationReady;
        talk.AutoAdvanceReceiveObserved -= OnAutoAdvanceReceiveObserved;
        talk.AutoAdvanceUiObserved -= OnAutoAdvanceUiObserved;
        talk.Advanced -= OnTalkClosed;
        talk.Hidden -= OnTalkClosed;
        talk.Finalized -= OnTalkClosed;
        client.TerritoryChanged -= OnTerritoryChanged;
        client.Logout -= OnLogout;
        condition.ConditionChange -= OnConditionChange;
        bootstrap.Ready -= OnRuntimeReady;
        bootstrap.VoiceDesignReady -= OnVoiceDesignReady;
        if (runtimeManager is not null) runtimeManager.SelectionChanged -= OnBackendSelectionChanged;
        if (scheduler is not null)
        {
            scheduler.LineProcessed -= OnSchedulerLineProcessed;
            scheduler.BecameIdle -= OnSchedulerIdle;
        }
        Task[] coordinatorOperations;
        Task[] frameworkDispatchOperations;
        var disposingFromFrameworkDispatch = disposingFromFrameworkThread;
        lock (coordinatorTaskGate)
        {
            coordinatorOperations = coordinatorTasks
                .Where(task => !frameworkDispatchTasks.Contains(task))
                .ToArray();
            frameworkDispatchOperations = disposingFromFrameworkDispatch
                ? []
                : frameworkDispatchTasks.ToArray();
        }
        if (coordinatorOperations.Length > 0)
            await BestEffortAsync("Coordinator maintenance", () => Task.WhenAll(coordinatorOperations));
        if (frameworkDispatchOperations.Length > 0)
            await BestEffortFrameworkAsync("Framework dispatch", () => Task.WhenAll(frameworkDispatchOperations));
        await BestEffortAsync("Auto-advance retry", StopAutoAdvanceRetryAsync);
        await BestEffortAsync("Debug Base-voice refresh", WaitForDebugBaseVoiceRefreshShutdownAsync);
        // Session cancellation was published before any asynchronous drain.
        // Never wait for an event-gate holder that may be waiting on this
        // framework thread during teardown.
        var schedulerToDispose = scheduler;
        if (schedulerToDispose is not null)
            await BestEffortAsync("Dub scheduler", () => schedulerToDispose.DisposeAsync().AsTask());
        var voiceDesignerInitializationToDispose = voiceDesignerInitialization;
        if (voiceDesignerInitializationToDispose is not null)
            await BestEffortAsync("VoiceDesign initialization", () => voiceDesignerInitializationToDispose);
        var domainPoolToDispose = domainPool;
        if (domainPoolToDispose is not null)
            await BestEffortAsync("Casting-domain pool", () => domainPoolToDispose.DisposeAsync().AsTask());
        var voiceDesignerToDispose = voiceDesigner;
        if (voiceDesignerToDispose is not null)
            await BestEffortAsync("VoiceDesign runtime", () => voiceDesignerToDispose.DisposeAsync().AsTask());
        BestEffortSync("Audio engine", () => audio?.Dispose());
        if (audio is null)
            BestEffortSync("FFXIV game mixer backend", () => gameMixerBackend?.Dispose());
        await BestEffortAsync("Lip-sync service", () => lipSync.DisposeAsync(disposingFromFrameworkThread).AsTask());
        BestEffortSync("Debug gate", debugGate.Dispose);
        BestEffortSync("Event gate", eventGate.Dispose);
        BestEffortSync("Coordinator cancellation", officialObservationShutdown.Dispose);

        if (failures.Count > 0)
            throw new AggregateException("Session coordinator teardown failed", failures);
    }
}
