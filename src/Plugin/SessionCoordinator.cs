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
    string Status,
    IReadOnlyList<DebugBaseVoiceOption> BaseVoices);

public sealed class SessionCoordinator : IAsyncDisposable
{
    private sealed record PendingAutoAdvance(
        long SessionEpoch,
        long LineSequence,
        long TalkSerial,
        string Speaker,
        string Text);

    private sealed record PendingNativeVoice(
        NativeVoiceObservation Observation,
        CutsceneSession Session,
        ActualTalkLine Talk,
        OfficialVoiceClipObservation? CorrelatedClip = null,
        ResolvedSpeaker? SpeakerSnapshot = null);

    private sealed record OfficialClipSnapshot(
        OfficialVoiceClipObservation Observation,
        CutsceneSession Session,
        ActualTalkLine Talk,
        ResolvedSpeaker SpeakerSnapshot,
        string Language);

    private sealed record PendingOfficialClip(
        OfficialVoiceClipObservation Observation,
        CutsceneSession Session,
        ActualTalkLine? Talk,
        DateTimeOffset ExpiresAt);

    private sealed record FrameworkStateSnapshot(
        string Language,
        uint TerritoryId,
        string? TerritoryPlaceName,
        bool InCutscene,
        bool InCombat,
        bool CanWork,
        bool CanProcessOfficialReferences);

    private const string DebugLineSource = "Resonance inference debug";
    private static readonly TimeSpan NativeVoiceGrace = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan TeardownFrameworkWait = TimeSpan.FromSeconds(5);
    private readonly CutsceneDetector cutscenes;
    private readonly TalkObserver talk;
    private readonly IClientState client;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly SpeakerResolver speakers;
    private readonly QuestDialoguePrefetcher prefetcher;
    private readonly NativeVoiceDetector nativeVoice;
    private readonly LipSyncService lipSync;
    private readonly VoiceRegistry voices;
    private readonly BootstrapService bootstrap;
    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly AutoAdvanceDiagnosticGate autoAdvanceDiagnostics = new();
    private readonly GameVolumeService gameVolume;
    private readonly LineCache lineCache;
    private readonly NativeVoiceRepository nativeVoices;
    private readonly Database database;
    private readonly ScdExtractor scdExtractor;
    private readonly string officialWorkingDirectory;
    private readonly CastingProfileCatalog catalog;
    private readonly OfficialVoiceCatalog officialVoiceCatalog;
    private readonly Func<uint, string?> territoryPlaceName;
    private readonly SemaphoreSlim eventGate = new(1, 1);
    private readonly SemaphoreSlim debugGate = new(1, 1);
    private readonly object debugCancellationGate = new();
    private readonly object debugRefreshGate = new();
    private readonly object autoAdvanceRetryGate = new();
    private readonly object nativeVoiceProcessingGate = new();
    private readonly object officialBuildGate = new();
    private readonly object officialObservationGate = new();
    private readonly object officialObservationInvalidationGate = new();
    private readonly object pendingOfficialClipGate = new();
    private readonly object officialPreparationGate = new();
    private readonly object backendSwitchGate = new();
    private readonly object coordinatorTaskGate = new();
    private readonly object disposalGate = new();
    private readonly AsyncLocal<Task?> activeFrameworkDispatch = new();
    private readonly CancellationTokenSource officialObservationShutdown = new();
    private readonly Dictionary<long, Task> officialObservationTasks = [];
    private readonly List<CancellationTokenSource> retiredOfficialObservationCancellations = [];
    private CancellationTokenSource currentOfficialObservationCancellation = new();
    private readonly Dictionary<string, Task> officialPreparationTasks = new(StringComparer.Ordinal);
    private readonly List<CancellationTokenSource> retiredOfficialPreparationCancellations = [];
    private CancellationTokenSource officialPreparationCancellation = new();
    private bool officialPreparationPaused;
    private long baseHotLoadSafetyGeneration;
    private AudioEngine? audio;
    private DubScheduler? scheduler;
    private VoiceDesigner? voiceDesigner;
    private Task? voiceDesignerInitialization;
    private RuntimeManager? runtimeManager;
    private CastingDomainPool? domainPool;
    private CutsceneSession? session;
    private long nextEpoch;
    private int disposed;
    private PendingNativeVoice? pendingNativeVoice;
    private OfficialClipSnapshot? recentOfficialClip;
    private readonly LinkedList<PendingOfficialClip> pendingOfficialClips = [];
    private Task? nativeVoiceProcessing;
    private long nextOfficialObservationTask;
    private PendingAutoAdvance? pendingAutoAdvance;
    private int autoAdvanceDispatching;
    private CancellationTokenSource? autoAdvanceRetryCancellation;
    private Task? autoAdvanceRetryTask;
    private long autoAdvanceRetryGeneration;
    private long? activeAutoAdvanceRetryGeneration;
    private readonly ConcurrentDictionary<string, StoredVoiceProfile> profileCache = new();
    private readonly ConcurrentDictionary<long, string> speakerKeys = new();
    private readonly ConcurrentDictionary<(long Epoch, long Serial), ResolvedSpeaker> lineSpeakerSnapshots = new();
    private OfficialReferenceBuilder? officialReferences;
    private CancellationTokenSource? officialBuildCancellation;
    private Task? officialBuildTask;
    private Task? officialBuildRetry;
    private int officialBuildPending;
    private int baseHotLoadSafe;
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
    public event Action<NativeVoiceObservation>? NativeVoiceObserved;
    public event Action<string, string>? SpeakerProfileUpgraded;
    public string? GetSpeakerProfile(string stableKey)
    {
        var language = Volatile.Read(ref frameworkLanguage) ?? "english";
        return profileCache.TryGetValue(ProfileCacheKey(stableKey, language), out var profile)
               && String.Equals(profile.Language, language, StringComparison.Ordinal)
            ? JsonSerializer.Serialize(profile)
            : null;
    }

    public SessionCoordinator(CutsceneDetector cutscenes, TalkObserver talk, IClientState client, ICondition condition,
        IFramework framework,
        SpeakerResolver speakers, QuestDialoguePrefetcher prefetcher, NativeVoiceDetector nativeVoice, LipSyncService lipSync,
        Database database, BootstrapService bootstrap, GameVolumeService gameVolume, string cacheDirectory,
        ScdExtractor scdExtractor, string officialWorkingDirectory,
        CastingProfileCatalog catalog,
        OfficialVoiceCatalog officialVoiceCatalog,
        Func<uint, string?> territoryPlaceName,
        Configuration configuration, IPluginLog log)
    {
        this.cutscenes = cutscenes;
        this.talk = talk;
        this.client = client;
        this.condition = condition;
        this.framework = framework;
        this.speakers = speakers;
        this.prefetcher = prefetcher;
        this.nativeVoice = nativeVoice;
        this.lipSync = lipSync;
        voices = new VoiceRegistry(database);
        this.database = database;
        this.scdExtractor = scdExtractor;
        this.officialWorkingDirectory = officialWorkingDirectory;
        this.catalog = catalog;
        this.officialVoiceCatalog = officialVoiceCatalog;
        this.territoryPlaceName = territoryPlaceName;
        nativeVoices = new NativeVoiceRepository(database);
        this.bootstrap = bootstrap;
        this.configuration = configuration;
        this.log = log;
        this.gameVolume = gameVolume;
        debugBaseVoices = officialVoiceCatalog.Groups
            .Select(value => new DebugBaseVoiceOption(value.Id, value.Label, false)).ToArray();
        lineCache = new LineCache(database, cacheDirectory, () => configuration.CacheLimitBytes);
        lineCache.Failed += error => log.Warning(error, "Line cache operation failed");

        cutscenes.Started += OnCutsceneStarted;
        cutscenes.Ended += OnCutsceneEnded;
        talk.LineChanged += OnLineChanged;
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
        nativeVoice.TalkVoiceStarted += OnNativeVoiceStarted;
        nativeVoice.OfficialVoiceClipObserved += OnOfficialVoiceClipObserved;
        QueueFrameworkAction(() =>
        {
            if (cutscenes.IsInCutscene) OnCutsceneStartedOnFramework();
        }, "Initial cutscene framework dispatch failed");
    }

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
        audio ??= new AudioEngine(configuration.AudioOutputDeviceNumber);
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
        audio.Finished += line => QueueFrameworkAction(
            () => OnAudioFinished(line), "Audio-finished framework dispatch failed");
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
        var language = CurrentLanguage();
        Volatile.Write(ref frameworkLanguage, language);
        scheduler = new DubScheduler(
            manager.Runtime,
            ResolveVoiceAsync,
            lineCache,
            manager.ModelHash,
            language,
            () => Math.Min(Math.Max(0, configuration.CacheLimitBytes), 256L * 1024 * 1024));
        officialReferences = new OfficialReferenceBuilder(
            database, voices, manager.Runtime, scdExtractor, officialWorkingDirectory, manager.ModelHash,
            manager.ExtractReferenceAsync);
        officialReferences.ProfileBuilt += OnOfficialProfileBuilt;
        ScheduleOfficialReferenceBuild();
        scheduler.LineBuffered += line => QueueFrameworkAction(
            () => OnLineBuffered(line), "Buffered-line framework dispatch failed");
        scheduler.PredictionStreamable += _ => QueueFrameworkAction(
            TryCompleteAutoAdvance, "Prediction-streamable framework dispatch failed");
        scheduler.LineFailed += (line, error) => log.Error(error, "Synthesis failed for line {Serial}", line.Sequence);
        scheduler.BecameIdle += () => QueueFrameworkAction(
            OnSchedulerIdle, "Scheduler-idle framework dispatch failed");
        UpdateBaseHotLoadSafetyOnFramework();
        RequestBaseHotLoadRestore();
    }

    private void OnVoiceDesignReady(string designPath, string codecPath)
    {
        var manager = bootstrap.RuntimeManager;
        var backend = manager?.Selection?.Effective.Name;
        if (manager is null || backend is null || Volatile.Read(ref disposed) != 0) return;
        Task initialization;
        lock (coordinatorTaskGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            initialization = Task.Run(async () =>
            {
                VoiceDesigner? created = null;
                try
                {
                    var frameworkState = await CaptureFrameworkStateAsync(officialObservationShutdown.Token)
                        .ConfigureAwait(false);
                    created = new VoiceDesigner(manager.Runtime, designPath, codecPath, backend,
                        manager.PluginLifetimeLease,
                        manager.ExtractReferenceAsync);
                    var latestBackend = manager.Selection?.Effective.Name;
                    if (latestBackend is not null && latestBackend != backend)
                        await created.SwitchBackendAsync(latestBackend, officialObservationShutdown.Token).ConfigureAwait(false);
                    if (Volatile.Read(ref disposed) != 0)
                    {
                        await created.DisposeAsync().ConfigureAwait(false);
                        return;
                    }
                    voiceDesigner = created;
                    await RefreshDebugBaseVoicesAsync(frameworkState.Language, officialObservationShutdown.Token)
                        .ConfigureAwait(false);
                    if (domainPool is null)
                    {
                        var pool = new CastingDomainPool(
                            voices,
                            catalog,
                            () => voiceDesigner,
                            () => true,
                            () => frameworkState.TerritoryPlaceName,
                            () => frameworkState.Language,
                            () => (configuration.ReadyMasculineVoices, configuration.ReadyFeminineVoices),
                            manager.ModelHash,
                            configuration.GetPromptOverride,
                            () => configuration.BackgroundCasting,
                            async token => (await CaptureFrameworkStateAsync(token).ConfigureAwait(false)).CanWork,
                            async token => (await CaptureFrameworkStateAsync(token).ConfigureAwait(false)).TerritoryPlaceName,
                            async token => (await CaptureFrameworkStateAsync(token).ConfigureAwait(false)).Language);
                        pool.Failed += error => log.Warning(error, "Background voice casting failed; retrying");
                        domainPool = pool;
                        pool.ActivateTerritory(frameworkState.TerritoryPlaceName);
                    }
                }
                catch (Exception error)
                {
                    if (created is not null && !ReferenceEquals(voiceDesigner, created))
                        await created.DisposeAsync().ConfigureAwait(false);
                    log.Error(error, "VoiceDesign runtime initialization failed");
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
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
        InvalidateBaseHotLoadSafetyOnFramework();
        CancelOfficialReferenceBuild();
        CancelOfficialPreparations();
        domainPool?.Pause();
        domainPool?.ResetActivation();
        domainPool?.ActivateTerritory(territoryPlaceName(client.TerritoryType));
        CancelSession();
        session = new CutsceneSession(Interlocked.Increment(ref nextEpoch), client.TerritoryType);
        prefetcher.BeginSession();
    }

    private void OnCutsceneEnded() => QueueFrameworkAction(
        OnCutsceneEndedOnFramework, "Cutscene-end framework dispatch failed");

    private void OnCutsceneEndedOnFramework()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        ResumeOfficialPreparations();
        domainPool?.Pause();
        CancelSession();
        UpdateBaseHotLoadSafetyOnFramework();
        RequestBaseHotLoadRestore();
        ScheduleOfficialReferenceBuild();
    }

    private void OnTerritoryChanged(uint territory) => QueueFrameworkAction(
        () => OnTerritoryChangedOnFramework(territory), "Territory-change framework dispatch failed");

    private void OnTerritoryChangedOnFramework(uint territory)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        InvalidateBaseHotLoadSafetyOnFramework();
        CancelOfficialReferenceBuild();
        CancelOfficialPreparations();
        domainPool?.Pause();
        domainPool?.ResetActivation();
        domainPool?.ActivateTerritory(territoryPlaceName(territory));
        CancelSession();
        ResumeOfficialPreparations();
        UpdateBaseHotLoadSafetyOnFramework();
        RequestBaseHotLoadRestore();
        ScheduleOfficialReferenceBuild();
    }

    private void OnLogout(int _, int __) => QueueFrameworkAction(
        OnLogoutOnFramework, "Logout framework dispatch failed");

    private void OnLogoutOnFramework()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        InvalidateBaseHotLoadSafetyOnFramework();
        CancelOfficialReferenceBuild();
        CancelOfficialPreparations();
        domainPool?.Pause();
        domainPool?.ResetActivation();
        CancelSession();
    }

    private void OnConditionChange(ConditionFlag flag, bool value) => QueueFrameworkAction(
        () => OnConditionChangeOnFramework(flag, value), "Condition-change framework dispatch failed");

    private void OnConditionChangeOnFramework(ConditionFlag flag, bool value)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (flag != ConditionFlag.InCombat) return;
        if (value)
        {
            InvalidateBaseHotLoadSafetyOnFramework();
            CancelOfficialReferenceBuild();
            CancelOfficialPreparations();
        }
        else
        {
            ResumeOfficialPreparations();
            UpdateBaseHotLoadSafetyOnFramework();
            RequestBaseHotLoadRestore();
            ScheduleOfficialReferenceBuild();
        }
    }

    private void ScheduleOfficialReferenceBuild()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        QueueFrameworkAction(ScheduleOfficialReferenceBuildOnFramework,
            "Official-reference scheduling framework dispatch failed");
    }

    private void ScheduleOfficialReferenceBuildOnFramework()
    {
        var builder = officialReferences;
        if (builder is null || Volatile.Read(ref disposed) != 0) return;
        if (Volatile.Read(ref exclusiveOperationActive) != 0)
        {
            Volatile.Write(ref officialBuildPending, 1);
            return;
        }
        if (!CanStartOfficialReferences())
        {
            Volatile.Write(ref officialBuildPending, 1);
            EnsureOfficialBuildRetry();
            return;
        }
        Volatile.Write(ref officialBuildPending, 0);
        CancellationTokenSource cancellation;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (officialBuildGate)
        {
            if (Volatile.Read(ref disposed) != 0 || officialBuildCancellation is not null) return;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                officialObservationShutdown.Token);
            officialBuildCancellation = cancellation;
            // Publish both state objects before starting the async body.  The
            // body may complete synchronously (for example, unsupported
            // language), so publishing after invocation leaves disposal with
            // no task to await.
            officialBuildTask = completion.Task;
        }
        _ = ProcessAsync();

        async Task ProcessAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token).ConfigureAwait(false);
                var frameworkState = await CaptureFrameworkStateAsync(cancellation.Token)
                    .ConfigureAwait(false);
                if (!frameworkState.CanWork)
                {
                    Volatile.Write(ref officialBuildPending, 1);
                    return;
                }
                var language = frameworkState.Language;
                LogVoiceLearning("Profile processing started Language={Language}", language);
                var manager = runtimeManager;
                if (manager is null)
                {
                    Volatile.Write(ref officialBuildPending, 1);
                    return;
                }
                await manager.EnsureReadyAsync(cancellation.Token).ConfigureAwait(false);
                frameworkState = await CaptureFrameworkStateAsync(cancellation.Token)
                    .ConfigureAwait(false);
                if (!frameworkState.CanProcessOfficialReferences)
                {
                    Volatile.Write(ref officialBuildPending, 1);
                    return;
                }
                await builder.ProcessPendingAsync(language, cancellation.Token).ConfigureAwait(false);
                LogVoiceLearning("Profile processing completed Language={Language}", language);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Volatile.Write(ref officialBuildPending, 1);
            }
            catch (Exception error)
            {
                Volatile.Write(ref officialBuildPending, 1);
                log.Warning(error, "Pending official voice processing failed; retrying when idle");
            }
            finally
            {
                var retry = false;
                lock (officialBuildGate)
                {
                    var ownsPublishedBuild = ReferenceEquals(officialBuildCancellation, cancellation);
                    if (ownsPublishedBuild)
                    {
                        officialBuildCancellation = null;
                    }
                    // Keep the published task visible until its completion
                    // source is completed.  Shutdown can therefore never
                    // observe an empty slot while this finalizer is still
                    // publishing its terminal state.
                    completion.TrySetResult();
                    if (ownsPublishedBuild)
                    {
                        if (ReferenceEquals(officialBuildTask, completion.Task))
                        {
                            officialBuildTask = null;
                            retry = Volatile.Read(ref officialBuildPending) != 0
                                && Volatile.Read(ref disposed) == 0
                                && !officialObservationShutdown.IsCancellationRequested;
                        }
                    }
                }
                try { cancellation.Dispose(); }
                catch (ObjectDisposedException) { }
                if (retry) EnsureOfficialBuildRetry();
            }
        }
    }

    private void EnsureOfficialBuildRetry()
    {
        lock (officialBuildGate)
        {
            if (Volatile.Read(ref disposed) != 0 || officialBuildRetry is not null) return;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            officialBuildRetry = completion.Task;
            _ = RetryOfficialBuildAsync(completion);
        }

        async Task RetryOfficialBuildAsync(TaskCompletionSource completion)
        {
            try
            {
                var delay = TimeSpan.FromSeconds(1);
                while (Volatile.Read(ref officialBuildPending) != 0
                       && Volatile.Read(ref disposed) == 0
                       && Volatile.Read(ref exclusiveOperationActive) == 0
                       && !officialObservationShutdown.IsCancellationRequested)
                {
                    await Task.Delay(delay, officialObservationShutdown.Token).ConfigureAwait(false);
                    ScheduleOfficialReferenceBuild();
                    if (Volatile.Read(ref officialBuildPending) == 0) break;
                    delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                }
            }
            catch (OperationCanceledException) when (officialObservationShutdown.IsCancellationRequested) { }
            catch (Exception error)
            {
                Volatile.Write(ref officialBuildPending, 1);
                log.Warning(error, "Pending official voice retry dispatch failed");
            }
            finally
            {
                var retry = false;
                lock (officialBuildGate)
                {
                    if (ReferenceEquals(officialBuildRetry, completion.Task))
                    {
                        // Complete before clearing the published task while
                        // holding the same gate used by shutdown.  A waiter
                        // either sees the live task or a completed, cleared
                        // task, never a gap between those states.
                        completion.TrySetResult();
                        officialBuildRetry = null;
                        retry = Volatile.Read(ref officialBuildPending) != 0
                            && Volatile.Read(ref disposed) == 0
                            && Volatile.Read(ref exclusiveOperationActive) == 0
                            && !officialObservationShutdown.IsCancellationRequested;
                    }
                }
                if (retry) EnsureOfficialBuildRetry();
            }
        }
    }

    private void CancelOfficialReferenceBuild()
    {
        if (runtimeManager?.UsesExternalBaseHost == true)
        {
            // The external Base host cannot cancel native reference extraction
            // safely. Let an already accepted extraction finish and persist;
            // the next safe-idle pass remains pending for any other clips.
            Volatile.Write(ref officialBuildPending, 1);
            return;
        }
        CancellationTokenSource? cancellation;
        lock (officialBuildGate)
        {
            cancellation = officialBuildCancellation;
        }
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task WaitForOfficialReferenceBuildIdleAsync(CancellationToken token)
    {
        Task? build;
        lock (officialBuildGate) build = officialBuildTask;
        if (build is not null) await build.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task WaitForOfficialReferenceShutdownAsync()
    {
        while (true)
        {
            Task? build;
            Task? retry;
            lock (officialBuildGate)
            {
                build = officialBuildTask;
                retry = officialBuildRetry;
            }
            if (build is null && retry is null) return;
            if (build is not null) await build.ConfigureAwait(false);
            if (retry is not null) await retry.ConfigureAwait(false);
        }
    }

    private async Task WaitForOfficialPreparationShutdownAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (officialPreparationGate) tasks = officialPreparationTasks.Values.ToArray();
            if (tasks.Length == 0)
            {
                CancellationTokenSource[] retired;
                lock (officialPreparationGate)
                {
                    retired = retiredOfficialPreparationCancellations.ToArray();
                    retiredOfficialPreparationCancellations.Clear();
                }
                foreach (var cancellation in retired) cancellation.Dispose();
                return;
            }
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (Exception error) { log.Warning(error, "Curated official preparation stopped during shutdown"); }
        }
    }

    private void CancelOfficialPreparations()
    {
        CancellationTokenSource cancellation;
        lock (officialPreparationGate)
        {
            officialPreparationPaused = true;
            cancellation = officialPreparationCancellation;
        }
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ResumeOfficialPreparations()
    {
        lock (officialPreparationGate)
        {
            officialPreparationPaused = false;
            if (officialPreparationCancellation.IsCancellationRequested)
            {
                retiredOfficialPreparationCancellations.Add(officialPreparationCancellation);
                officialPreparationCancellation = new CancellationTokenSource();
            }
        }
    }

    private async Task PauseOfficialPreparationsAsync()
    {
        CancelOfficialPreparations();
        await WaitForOfficialPreparationShutdownAsync().ConfigureAwait(false);
    }

    private void OnSchedulerIdle()
    {
        UpdateBaseHotLoadSafetyOnFramework();
        RequestBaseHotLoadRestore();
        ScheduleOfficialReferenceBuild();
    }

    private void UpdateBaseHotLoadSafetyOnFramework()
    {
        var safe = !cutscenes.IsInCutscene
                   && !condition[ConditionFlag.InCombat]
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

    private bool CanStartOfficialReferences() =>
        !cutscenes.IsInCutscene && !condition[ConditionFlag.InCombat] && scheduler?.HasUrgentWork != true
        && bootstrap.State == BootstrapState.Ready && runtimeManager?.IsSwitching != true
        && Volatile.Read(ref debugRunning) == 0 && Volatile.Read(ref debugPlaybackActive) == 0
        && Volatile.Read(ref exclusiveOperationActive) == 0;

    private bool CanProcessOfficialReferences() =>
        CanStartOfficialReferences() && runtimeManager?.IsReady == true;

    private void OnLineChanged(ActualTalkLine line) => QueueFrameworkAction(
        () => OnLineChangedOnFramework(line), "Talk-line framework dispatch failed");

    private void OnLineChangedOnFramework(ActualTalkLine line)
    {
        if (Volatile.Read(ref disposed) != 0 || !configuration.Enabled
            || (!cutscenes.IsInCutscene && scheduler?.HasUrgentWork != true)
            || session is null || scheduler is null) return;
        InvalidateBaseHotLoadSafetyOnFramework();
        var capturedSession = session;
        InvalidateOfficialObservationSnapshots();
        ResolvedSpeaker capturedResolved;
        string language;
        string? firstTerritory;
        try
        {
            capturedResolved = SnapshotResolvedSpeaker(speakers.Resolve(line, capturedSession.Epoch));
            language = CurrentLanguage();
            firstTerritory = territoryPlaceName(capturedSession.TerritoryId);
        }
        catch (Exception error)
        {
            log.Warning(error, "Talk line ignored because framework speaker snapshot failed");
            return;
        }
        // Publish the immutable actor/evidence snapshot before asynchronous
        // line promotion starts. Native clips can arrive in that interval;
        // they must never reread a mutable actor or same-name replacement.
        lineSpeakerSnapshots[(capturedSession.Epoch, line.Serial)] = capturedResolved;
        var reconciledOfficialClip = TryReconcilePendingOfficialClip(
            line, capturedSession, capturedResolved, language);
        audio?.Stop();
        lipSync.Stop();
        Interlocked.Exchange(ref pendingNativeVoice, null);
        if (!reconciledOfficialClip) Interlocked.Exchange(ref recentOfficialClip, null);
        Interlocked.Exchange(ref pendingAutoAdvance, null);
        CancelAutoAdvanceRetry();
        CancelActualLines();
        QueueLineHandling(line, capturedSession, capturedResolved, language, firstTerritory);
    }

    private void QueueLineHandling(ActualTalkLine line, CutsceneSession capturedSession,
        ResolvedSpeaker capturedResolved, string language, string? firstTerritory)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (coordinatorTaskGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            coordinatorTasks.Add(completion.Task);
        }
        _ = ObserveLineHandlingAsync(completion, line, capturedSession, capturedResolved, language, firstTerritory);
    }

    private async Task ObserveLineHandlingAsync(TaskCompletionSource completion, ActualTalkLine line,
        CutsceneSession capturedSession, ResolvedSpeaker capturedResolved, string language, string? firstTerritory)
    {
        try
        {
            await HandleLineAsync(line, capturedSession, capturedResolved, language, firstTerritory)
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
        string language, string? firstTerritory)
    {
        using var lineCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            capturedSession.CancellationToken, officialObservationShutdown.Token);
        var operationToken = lineCancellation.Token;
        await eventGate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0) return;
            // Cutscene/combat transitions cancel curated preparation before
            // urgent synthesis is allowed to proceed.  The canceled task is
            // fully observed here because framework event handlers cannot
            // block their callback thread on an async drain.
            try
            {
                await WaitForOfficialPreparationShutdownAsync()
                    .WaitAsync(TimeSpan.FromMilliseconds(500), operationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                log.Debug("Continuing urgent Talk synthesis while official preparation is still draining");
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
                return;
            }
            var current = capturedSession;
            var currentScheduler = scheduler;
            if (currentScheduler is null || !IsCurrent(current)) return;
            var resolved = capturedResolved;
            lineSpeakerSnapshots[(current.Epoch, actual.Serial)] = resolved;
            // A known official alias is a deliberate identity exception to the
            // scene-local rule.  It gets the catalog's canonical key even when
            // no live actor is present; arbitrary scene-local names remain
            // transient and never create a speaker row.
            var officialGroup = resolved.SceneLocal
                ? officialVoiceCatalog.Resolve(resolved.NpcBaseId, resolved.DisplayName, language)
                : null;
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
                using var cheapOfficialCancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                cheapOfficialCancellation.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    await StartOfficialGroupPreparation(stored.Id, resolved, language, allowBuild: true,
                        token: cheapOfficialCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cheapOfficialCancellation.IsCancellationRequested)
                {
                    log.Debug("Deferring official profile attachment after the cheap lookup budget expired");
                }
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
                stored?.DisplayName ?? resolved.DisplayName,
                stored?.Id ?? 0,
                resolved.Evidence,
                casting,
                resolved.Sex,
                resolved.Archetype,
                resolved.ActorAddress,
                slot.Id,
                language);
            var line = current.PromotePrediction(actual.Speaker, actual.Text, assignment);
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
            var currentSpeakerSnapshot = lineSpeakerSnapshots.TryGetValue(
                (current.Epoch, actual.Serial), out var resolvedSnapshot)
                ? resolvedSnapshot
                : null;
            var pendingNativeSnapshot = Volatile.Read(ref pendingNativeVoice);
            var nativeAlreadyCorrelated = line.NativeVoiceStatus == NativeVoiceStatus.NativeVoiced
                || MatchesNativeVoice(pendingNativeSnapshot, current, actual, currentSpeakerSnapshot);
            if (!nativeAlreadyCorrelated)
            {
                var graceRemaining = NativeVoiceGrace - (DateTimeOffset.UtcNow - actual.ObservedAt);
                if (graceRemaining > TimeSpan.Zero)
                    await Task.Delay(graceRemaining, operationToken).ConfigureAwait(false);
                var holdUntil = nativeVoice.GetActiveOfficialClipHoldUntil(actual.ObservedAt);
                if (holdUntil is { } activeUntil)
                {
                    var holdRemaining = activeUntil - DateTimeOffset.UtcNow;
                    if (holdRemaining > TimeSpan.Zero)
                        await Task.Delay(holdRemaining, operationToken).ConfigureAwait(false);
                }
            }
            if (!IsCurrent(current))
            {
                line.Cancel(DubLineState.Invalidated);
                return;
            }
            var pendingNative = Interlocked.Exchange(ref pendingNativeVoice, null);
            var nativeDetected = line.NativeVoiceStatus == NativeVoiceStatus.NativeVoiced
                || MatchesNativeVoice(pendingNative, current, actual, currentSpeakerSnapshot);
            if (nativeDetected)
            {
                if (!line.TryMarkNativeVoiced(
                        DubLineState.Predicted,
                        DubLineState.VoiceResolving,
                        DubLineState.Queued,
                        DubLineState.Generating,
                        DubLineState.Buffered,
                        DubLineState.Active))
                {
                    line.Cancel(DubLineState.Invalidated);
                    return;
                }
            }
            else
            {
                if (!line.TryMarkNotVoiced()) return;
                if (promotedPrediction && line.State == DubLineState.Buffered && line.Audio.ProducerCompleted)
                    QueueFrameworkAction(() => OnLineBuffered(line),
                        "Promoted-line framework dispatch failed");
                else if (!promotedPrediction || line.State == DubLineState.Predicted)
                    currentScheduler.Enqueue(line);
            }

            var update = prefetcher.Observe(actual.Speaker, actual.Text);
            if (line.NativeVoiceStatus == NativeVoiceStatus.NativeVoiced)
            {
                current.ReplacePredictions([]);
            }
            else if (update.Synchronized)
            {
                var predicted = current.ReplacePredictions(update.Future.Select(value =>
                    ($"predicted:{value.Speaker}", value.Speaker, value.Text)), language);
                foreach (var future in predicted)
                {
                    if (!IsCurrent(current))
                    {
                        future.Cancel(DubLineState.Invalidated);
                        return;
                    }
                    var futureKey = $"scene:{current.Epoch}:{future.SpeakerName.ToLowerInvariant()}";
                    var futureEvidence = new SpeakerCastingEvidence(
                        futureKey, FirstTerritoryPlaceName: firstTerritory, Sex: "masculine");
                    future.SpeakerKey = futureKey;
                    future.SpeakerId = null;
                    future.VoiceArchetype = "neutral_adult";
                    future.VoiceSex = "masculine";
                    future.ActorAddress = 0;
                    future.CastingEvidence = futureEvidence;
                    future.TransientSpeaker = true;
                    future.Casting = catalog.Resolve(futureEvidence);
                    future.CastingSlotId = catalog.SelectBestSlot(future.Casting, futureEvidence).Id;
                    // Predictions stay in-memory. No speaker row, casting row,
                    // pool claim, or designed profile exists until promotion.
                    currentScheduler.Enqueue(future);
                }
            }
            else if (update.Resynchronized)
            {
                current.ReplacePredictions([]);
            }
        }
        catch (Exception error) { log.Error(error, "Failed to schedule Talk line"); }
        finally { eventGate.Release(); }
    }

    private async ValueTask<VoiceResolution> ResolveVoiceAsync(DubLine line, CancellationToken token)
    {
        var language = line.Language
            ?? throw new InvalidOperationException("Queued line has no resolved dubbing language");
        if (line.TransientSpeaker)
            return await ResolveTransientVoiceAsync(line, language, token).ConfigureAwait(false);
        if (line.ActualStatus == ActualStatus.Predicted)
        {
            var predictedProfile = profileCache.TryGetValue(ProfileCacheKey(line.SpeakerKey, language), out var cached)
                ? cached
                : await voices.GetBestVoiceByStableKeyAsync(line.SpeakerKey, language, token).ConfigureAwait(false);
            if (predictedProfile is not null)
            {
                line.VoiceProfileId = predictedProfile.Id;
                line.VoiceProfileHash = predictedProfile.ProfileHash;
                profileCache[ProfileCacheKey(line.SpeakerKey, language)] = predictedProfile;
                return VoiceResolution.Ready(predictedProfile.Reference);
            }

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
        var stored = await voices.GetBestVoiceAsync(speakerId, language, token).ConfigureAwait(false);
        if (stored is not null)
        {
            line.VoiceProfileId = stored.Id;
            line.VoiceProfileHash = stored.ProfileHash;
            profileCache[ProfileCacheKey(line.SpeakerKey, language)] = stored;
            return VoiceResolution.Ready(stored.Reference);
        }
        var casting = line.Casting ?? throw new InvalidOperationException("Actual line has no casting resolution");
        var knownTraits = line.CastingEvidence is null ? null : JsonSerializer.Serialize(line.CastingEvidence);
        var pooled = await voices.TryAssignDomainPoolVoiceAsync(
            speakerId, casting.DomainId, language, line.VoiceSex, knownTraits, token).ConfigureAwait(false);
        if (pooled is not null)
        {
            line.VoiceProfileId = pooled.Id;
            line.VoiceProfileHash = pooled.ProfileHash;
            profileCache[ProfileCacheKey(line.SpeakerKey, language)] = pooled;
            QueueProfileUpgradeNotification(line.SpeakerKey, pooled.Id);
            return VoiceResolution.Ready(pooled.Reference);
        }
        domainPool?.RequestMissingResolution(casting, language, line.VoiceSex, followsSpeaker: true);
        var designer = voiceDesigner;
        if (designer is null) throw new InvalidOperationException("No prepared voice and VoiceDesign is still downloading");
        var evidence = line.CastingEvidence ?? new SpeakerCastingEvidence(line.SpeakerKey, Sex: line.VoiceSex);
        var slot = line.CastingSlotId is null
            ? catalog.SelectBestSlot(casting, evidence)
            : catalog.GetSlot(casting.DomainId, line.VoiceSex, line.CastingSlotId);
        line.CastingSlotId = slot.Id;
        var instruction = configuration.GetPromptOverride(casting.DomainId, language, line.VoiceSex);
        instruction = String.IsNullOrWhiteSpace(instruction)
            ? catalog.BuildPrompt(casting, language, line.VoiceSex, slot.Id, evidence)
            : instruction.Trim();
        var seed = StableSeed($"{line.SpeakerKey}\0{language}\0{casting.DomainId}\0{line.VoiceSex}");
        if (line.ActualStatus == ActualStatus.Actual)
        {
            // A first-line miss is urgent cutscene work. Stream the line
            // directly; Base-reference construction belongs to safe-idle
            // domain refill and must not tear down Base inference here.
            await designer.SynthesizeDesignedLineOnlyAsync(
                line.Text, instruction, seed, language, line.Audio, token).ConfigureAwait(false);
            line.DirectSynthesisCompleted = true;
            return VoiceResolution.Ready(null);
        }

        var reference = await designer.DesignReferenceAsync(instruction, seed, language, token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var profile = VoiceRegistry.CreateProfile(
            VoiceProfileKind.Designed, language,
            (runtimeManager ?? throw new InvalidOperationException("Base runtime is unavailable")).ModelHash,
            catalog.Version, instruction, seed, reference,
            sourceMetadata: JsonSerializer.Serialize(new
            {
                domain = casting.DomainId,
                sex = line.VoiceSex,
                slot = slot.Id,
                modifiers = casting.ModifierIds,
            }),
            domainId: casting.DomainId,
            catalogVersion: catalog.Version,
            traitsJson: JsonSerializer.Serialize(slot));
        token.ThrowIfCancellationRequested();
        profile = await voices.SaveAndAssignAsync(speakerId, profile, token).ConfigureAwait(false);
        profileCache[ProfileCacheKey(line.SpeakerKey, language)] = profile;
        line.VoiceProfileId = profile.Id;
        line.VoiceProfileHash = profile.ProfileHash;
        QueueProfileUpgradeNotification(line.SpeakerKey, profile.Id);
        return VoiceResolution.Ready(reference);
    }

    private async ValueTask<VoiceResolution> ResolveTransientVoiceAsync(
        DubLine line, string language, CancellationToken token)
    {
        var designer = voiceDesigner ?? throw new InvalidOperationException("VoiceDesign is still downloading");
        var evidence = line.CastingEvidence ?? new SpeakerCastingEvidence(line.SpeakerKey,
            Sex: line.VoiceSex);
        var casting = line.Casting ?? catalog.Resolve(evidence);
        line.Casting = casting;
        var slot = line.CastingSlotId is null
            ? catalog.SelectBestSlot(casting, evidence)
            : catalog.GetSlot(casting.DomainId, line.VoiceSex, line.CastingSlotId);
        line.CastingSlotId = slot.Id;
        var instruction = configuration.GetPromptOverride(casting.DomainId, language, line.VoiceSex);
        instruction = String.IsNullOrWhiteSpace(instruction)
            ? catalog.BuildPrompt(casting, language, line.VoiceSex, slot.Id, evidence)
            : instruction.Trim();
        var seed = StableSeed($"{line.SpeakerKey}\0{language}\0{casting.DomainId}\0{line.VoiceSex}");
        if (line.ActualStatus == ActualStatus.Actual)
        {
            await designer.SynthesizeDesignedLineOnlyAsync(line.Text, instruction, seed, language, line.Audio, token)
                .ConfigureAwait(false);
            line.DirectSynthesisCompleted = true;
            return VoiceResolution.Ready(null);
        }

        // Predictions have no durable identity.  Stream a transient line
        // directly from VoiceDesign so Base-reference extraction cannot block
        // urgent cutscene work or consume a pool/profile.  If the prediction
        // is promoted, CutsceneSession either invalidates this work for a
        // known actor or preserves it only for the still-scene-local case.
        await designer.SynthesizeDesignedLineOnlyAsync(
                line.Text, instruction, seed, language, line.Audio, token)
            .ConfigureAwait(false);
        line.DirectSynthesisCompleted = true;
        return VoiceResolution.Ready(null);
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
        var canWork = !inCutscene && !inCombat && scheduler?.HasUrgentWork != true
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
            inCutscene, inCombat, canWork,
            canWork && manager?.IsReady == true);
    }

    private static string ProfileCacheKey(string stableKey, string language) =>
        $"{stableKey}\0{language}";

    private void LogVoiceLearning(string message, params object[] args)
    {
        if (configuration.VoiceLearningDiagnostics) log.Debug($"Voice learning: {message}", args);
    }

    private void LogAutoAdvanceDiagnostic(string message, params object[] args)
    {
        if (configuration.AutoAdvanceDiagnostics) log.Debug($"Auto-advance diagnostics: {message}", args);
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
        if (line.ActualStatus == ActualStatus.Predicted)
        {
            TryCompleteAutoAdvance();
            return;
        }
        var current = talk.Current;
        if (current is null || session?.Epoch != line.SessionEpoch || line.ActualStatus != ActualStatus.Actual) return;
        // Playback authority: actual Talk remains visible and exact text still matches.
        if (current.Text != line.Text || current.Speaker != line.SpeakerName) return;
        audio?.Play(line, gameVolume.GetVoiceGain(configuration.Volume));
        ScheduleOfficialReferenceBuild();
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
            || session is not { } current) return;
        Interlocked.Exchange(ref pendingAutoAdvance,
            new(current.Epoch, line.Sequence, serial, line.SpeakerName, line.Text));
        TryCompleteAutoAdvance();
        ScheduleOfficialReferenceBuild();
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
        if (!AutoAdvancePolicy.IsImmediateNextPredictionPlayable(current.Lines, pending.LineSequence))
        {
            // The next prediction may still be resolving.  Keep one bounded
            // retry task alive so readiness events are not the sole progress
            // path; the task exits on Talk/session invalidation or disposal.
            ScheduleAutoAdvanceRetry(pending);
            return;
        }
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

    private async Task WaitForNativeVoiceProcessingAsync()
    {
        Task? processing;
        lock (nativeVoiceProcessingGate) processing = nativeVoiceProcessing;
        if (processing is null) return;
        try { await processing.ConfigureAwait(false); }
        catch (Exception error) { log.Warning(error, "Native VO processing task failed during shutdown"); }
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
        audio?.Stop();
        lipSync.Stop();
        CancelActualLines();
        ScheduleOfficialReferenceBuild();
    }

    private void CancelActualLines()
    {
        if (session is not { } current) return;
        foreach (var line in current.Lines.Where(line => line.ActualStatus == ActualStatus.Actual && !line.IsTerminal))
            line.Cancel(DubLineState.Invalidated);
    }

    private void OnNativeVoiceStarted(NativeVoiceObservation observation)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        QueueFrameworkAction(() => HandleNativeVoiceStarted(observation),
            "Native VO observation dispatch failed");
    }

    private void HandleNativeVoiceStarted(NativeVoiceObservation observation)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var currentLine = talk.Current;
        var currentSession = Volatile.Read(ref session);
        if (currentSession is null || currentLine is null) return;
        var age = observation.StartedAt - currentLine.ObservedAt;
        if (observation.CorrelatedClip is null
            && (age < TimeSpan.FromMilliseconds(-250) || age > TimeSpan.FromSeconds(1))) return;
        var speakerSnapshot = lineSpeakerSnapshots.TryGetValue(
            (currentSession.Epoch, currentLine.Serial), out var resolvedSnapshot)
            ? resolvedSnapshot
            : null;
        var clip = Volatile.Read(ref recentOfficialClip);
        var correlatedClip = observation.CorrelatedClip is { } exactClip
                             && String.Equals(observation.ScdPath, exactClip.ScdPath,
                                 StringComparison.OrdinalIgnoreCase)
                             && observation.SoundNumber == exactClip.SoundNumber
            ? exactClip
            : clip is not null && MatchesOfficialClip(
                observation, currentSession, currentLine, clip, speakerSnapshot)
                ? clip.Observation
                : null;
        var pending = new PendingNativeVoice(observation, currentSession, currentLine, correlatedClip, speakerSnapshot);
        Interlocked.Exchange(ref pendingNativeVoice, pending);
        QueueNativeVoiceProcessing(pending);
    }

    private void QueueNativeVoiceProcessing(PendingNativeVoice pending)
    {
        lock (nativeVoiceProcessingGate)
        {
            // Never run suppression/session work inline on the native audio
            // hook. The hook only captures the immutable pending snapshot;
            // this continuation may inspect game/session state off-hook.
            var previous = nativeVoiceProcessing;
            nativeVoiceProcessing = Task.Run(() => ProcessNativeVoiceAsync(pending, previous));
        }
    }

    private async Task ProcessNativeVoiceAsync(PendingNativeVoice pending, Task? previous)
    {
        try
        {
            if (previous is not null)
            {
                try { await previous.ConfigureAwait(false); }
                catch (Exception error) { log.Warning(error, "Previous native VO processing task failed"); }
            }
            if (Volatile.Read(ref disposed) != 0) return;
            await eventGate.WaitAsync(officialObservationShutdown.Token).ConfigureAwait(false);
            try
            {
                var dispatch = framework.RunOnFrameworkThread(() =>
                    {
                        NativeVoiceObserved?.Invoke(pending.Observation);
                        ApplyNativeVoiceSuppressionLocked(pending);
                    });
                await FrameworkDispatchObserver.AwaitAsync(dispatch, officialObservationShutdown.Token, log,
                    "Native VO observation framework dispatch failed").ConfigureAwait(false);
            }
            finally { eventGate.Release(); }
        }
        catch (Exception error)
        {
            log.Warning(error, "Native VO observation processing failed");
        }
    }

    private void ApplyNativeVoiceSuppressionLocked(PendingNativeVoice pending)
    {
        if (Volatile.Read(ref disposed) != 0
            || !ReferenceEquals(Volatile.Read(ref pendingNativeVoice), pending)
            || pending.CorrelatedClip is null)
            return;
        var currentSession = Volatile.Read(ref session);
        var currentTalk = talk.Current;
        var currentSpeakerSnapshot = currentSession is not null && currentTalk is not null
            && lineSpeakerSnapshots.TryGetValue((currentSession.Epoch, currentTalk.Serial), out var resolvedSnapshot)
            ? resolvedSnapshot
            : null;
        if (currentSession is null || currentTalk is null
            || !MatchesNativeVoice(pending, currentSession, currentTalk, currentSpeakerSnapshot)) return;
        var candidate = currentSession.Lines
            .Where(line => line.ActualStatus == ActualStatus.Actual && !line.IsTerminal)
            .Where(line => line.ActualTalkSerial == currentTalk.Serial)
            .Where(line => String.Equals(line.Text, currentTalk.Text, StringComparison.Ordinal))
            .OrderByDescending(line => line.Sequence)
            .FirstOrDefault();
        if (candidate is null) return;
        currentSession.ReplacePredictions([]);
        Interlocked.CompareExchange(ref pendingNativeVoice, null, pending);
        if (!candidate.TryMarkNativeVoiced(
                DubLineState.Predicted,
                DubLineState.VoiceResolving,
                DubLineState.Queued,
                DubLineState.Generating,
                DubLineState.Buffered,
                DubLineState.Active)) return;
        scheduler?.NativeVoiceStarted(currentSession.Epoch, candidate.Sequence);
        audio?.Stop();
        lipSync.Stop();
        IsSpeaking = false;
        log.Debug("Native VO suppressed synthetic line {Sequence}: {Path}", candidate.Sequence,
            pending.Observation.ScdPath);
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

    private bool MatchesNativeVoice(
        PendingNativeVoice? pending, CutsceneSession currentSession, ActualTalkLine currentTalk,
        ResolvedSpeaker? currentSpeakerSnapshot = null)
    {
        if (pending is null || pending.CorrelatedClip is null || !ReferenceEquals(pending.Session, currentSession)
            || pending.Talk.Serial != currentTalk.Serial
            || !String.Equals(pending.Talk.Speaker, currentTalk.Speaker, StringComparison.Ordinal)
            || !String.Equals(pending.Talk.Text, currentTalk.Text, StringComparison.Ordinal)) return false;
        if (pending.Observation.SoundNumber is not { } soundNumber
            || soundNumber != pending.CorrelatedClip.SoundNumber) return false;
        if (pending.SpeakerSnapshot is { } expected && currentSpeakerSnapshot is { } actual
            && !SameActorEvidence(expected, actual)) return false;
        if (pending.Observation.CorrelatedClip is not null) return true;
        var age = pending.Observation.StartedAt - currentTalk.ObservedAt;
        return age >= TimeSpan.FromMilliseconds(-250) && age <= TimeSpan.FromSeconds(1);
    }

    private static bool MatchesOfficialClip(
        NativeVoiceObservation native, CutsceneSession sessionSnapshot, ActualTalkLine talkSnapshot,
        OfficialClipSnapshot clip, ResolvedSpeaker? nativeSpeakerSnapshot = null)
    {
        if (!ReferenceEquals(clip.Session, sessionSnapshot) || clip.Talk.Serial != talkSnapshot.Serial
            || !String.Equals(native.ScdPath, clip.Observation.ScdPath, StringComparison.OrdinalIgnoreCase)
            || native.SoundNumber is not { } soundNumber
            || soundNumber != clip.Observation.SoundNumber) return false;
        if (native.CorrelatedClip is { } exactClip
            && !ReferenceEquals(exactClip, clip.Observation)) return false;
        if (nativeSpeakerSnapshot is { } expected && clip.SpeakerSnapshot is { } actual
            && !SameActorEvidence(expected, actual)) return false;
        if (native.CorrelatedClip is not null) return true;
        var delta = native.StartedAt - clip.Observation.StartedAt;
        return delta.Duration() <= NativeVoiceGrace;
    }

    private void OnOfficialVoiceClipObserved(OfficialVoiceClipObservation observation)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        QueueFrameworkAction(() => HandleOfficialVoiceClipObserved(observation),
            "Official voice observation dispatch failed");
    }

    private void HandleOfficialVoiceClipObserved(OfficialVoiceClipObservation observation)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        LogVoiceLearning("Clip observed Path={Path} Sound={Sound} StartedAt={StartedAt}",
            observation.ScdPath, observation.SoundNumber, observation.StartedAt);
        if (!cutscenes.IsInCutscene)
        {
            LogVoiceLearning("Clip rejected Reason=outside-cutscene Path={Path} Sound={Sound}",
                observation.ScdPath, observation.SoundNumber);
            return;
        }
        var current = Volatile.Read(ref session);
        if (current is null)
        {
            LogVoiceLearning("Clip rejected Reason=no-session Path={Path} Sound={Sound}",
                observation.ScdPath, observation.SoundNumber);
            return;
        }
        var talkSnapshot = talk.Current;
        if (talkSnapshot is null)
        {
            RetainPendingOfficialClip(observation, current, null);
            LogVoiceLearning("Clip retained Reason=no-current-talk Epoch={Epoch} Path={Path} Sound={Sound}",
                current.Epoch, observation.ScdPath, observation.SoundNumber);
            return;
        }
        var talkAge = observation.StartedAt - talkSnapshot.ObservedAt;
        if (talkAge < TimeSpan.FromMilliseconds(-250) || talkAge > TimeSpan.FromSeconds(1))
        {
            LogVoiceLearning("Clip rejected Reason=stale-talk-snapshot Epoch={Epoch} TalkSerial={TalkSerial} AgeSeconds={AgeSeconds:F3} Path={Path} Sound={Sound}",
                current.Epoch, talkSnapshot.Serial, talkAge.TotalSeconds, observation.ScdPath,
                observation.SoundNumber);
            return;
        }
        ResolvedSpeaker speakerSnapshot;
        string language;
        try
        {
            if (!lineSpeakerSnapshots.TryGetValue(
                    (current.Epoch, talkSnapshot.Serial), out var resolvedSnapshot))
            {
                // Never resolve mutable actor/object state after the native
                // callback.  A missing immutable line snapshot is ambiguous
                // across despawn/respawn and same-name actors; a later valid
                // observation may repair the source safely.
                RetainPendingOfficialClip(observation, current, talkSnapshot);
                LogVoiceLearning("Clip retained Reason=no-immutable-speaker-snapshot Epoch={Epoch} TalkSerial={TalkSerial} Path={Path} Sound={Sound}",
                    current.Epoch, talkSnapshot.Serial, observation.ScdPath, observation.SoundNumber);
                return;
            }
            speakerSnapshot = resolvedSnapshot;
            language = CurrentLanguage();
        }
        catch (Exception error)
        {
            log.Warning(error, "Official voice clip rejected because callback identity could not be snapshotted");
            return;
        }
        var snapshot = new OfficialClipSnapshot(observation, current, talkSnapshot, speakerSnapshot, language);
        PublishOfficialClipSnapshot(snapshot);
    }

    private void RetainPendingOfficialClip(
        OfficialVoiceClipObservation observation, CutsceneSession current, ActualTalkLine? talkSnapshot)
    {
        var pending = new PendingOfficialClip(
            observation, current, talkSnapshot, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5));
        lock (pendingOfficialClipGate)
        {
            ExpirePendingOfficialClipsLocked(DateTimeOffset.UtcNow);
            if (pendingOfficialClips.Any(existing =>
                    ReferenceEquals(existing.Session, current)
                    && String.Equals(existing.Observation.ScdPath, observation.ScdPath,
                        StringComparison.OrdinalIgnoreCase)
                    && existing.Observation.SoundNumber == observation.SoundNumber
                    && existing.Observation.StartedAt == observation.StartedAt)) return;
            pendingOfficialClips.AddLast(pending);
            while (pendingOfficialClips.Count > 32) pendingOfficialClips.RemoveFirst();
        }
    }

    private bool TryReconcilePendingOfficialClip(
        ActualTalkLine line, CutsceneSession current, ResolvedSpeaker speakerSnapshot, string language)
    {
        PendingOfficialClip? pending = null;
        lock (pendingOfficialClipGate)
        {
            ExpirePendingOfficialClipsLocked(DateTimeOffset.UtcNow);
            var node = pendingOfficialClips.First;
            while (node is not null)
            {
                var next = node.Next;
                var candidate = node.Value;
                if (!ReferenceEquals(candidate.Session, current))
                {
                    node = next;
                    continue;
                }
                if (candidate.Talk is { } capturedTalk && capturedTalk.Serial != line.Serial)
                {
                    node = next;
                    continue;
                }
                var age = candidate.Observation.StartedAt - line.ObservedAt;
                if (age < TimeSpan.FromMilliseconds(-250))
                {
                    node = next;
                    continue;
                }
                if (age > TimeSpan.FromSeconds(1))
                {
                    pendingOfficialClips.Remove(node);
                    node = next;
                    continue;
                }
                pending = candidate;
                pendingOfficialClips.Remove(node);
                break;
            }
        }
        if (pending is null) return false;
        // The raw path and SoundNumber remain part of the immutable clip and
        // are used by native playback correlation after this reconciliation.
        PublishOfficialClipSnapshot(new OfficialClipSnapshot(
            pending.Observation, current, line, speakerSnapshot, language));
        return true;
    }

    private void ExpirePendingOfficialClipsLocked(DateTimeOffset now)
    {
        var node = pendingOfficialClips.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.ExpiresAt <= now) pendingOfficialClips.Remove(node);
            node = next;
        }
    }

    private void PublishOfficialClipSnapshot(OfficialClipSnapshot snapshot)
    {
        Interlocked.Exchange(ref recentOfficialClip, snapshot);
        var pending = Volatile.Read(ref pendingNativeVoice);
        if (pending is not null && MatchesOfficialClip(
                pending.Observation, pending.Session, pending.Talk, snapshot, pending.SpeakerSnapshot))
        {
            var correlated = pending with { CorrelatedClip = snapshot.Observation };
            if (ReferenceEquals(Interlocked.CompareExchange(ref pendingNativeVoice, correlated, pending), pending))
                QueueNativeVoiceProcessing(correlated);
        }
        CaptureOfficialObservation(snapshot);
    }

    private void CaptureOfficialObservation(OfficialClipSnapshot snapshot)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try
        {
            TrackOfficialObservation(snapshot.Observation, snapshot.Session, snapshot.Talk,
                snapshot.SpeakerSnapshot, snapshot.Language);
        }
        catch (Exception error)
        {
            log.Warning(error, "Official voice clip rejected because speaker evidence could not be snapshotted");
        }
    }

    private void TrackOfficialObservation(OfficialVoiceClipObservation observation,
        CutsceneSession sessionSnapshot, ActualTalkLine talkSnapshot, ResolvedSpeaker resolvedSnapshot,
        string language)
    {
        var id = Interlocked.Increment(ref nextOfficialObservationTask);
        lock (officialObservationGate)
        {
            // Acceptance and shutdown publication share one gate.  A native
            // callback that crossed the pre-dispatch disposed check either
            // registers here before shutdown snapshots the task set, or is
            // rejected after shutdown has begun; it can never become an
            // unawaited database writer during teardown.
            if (Volatile.Read(ref disposed) != 0
                || officialObservationShutdown.IsCancellationRequested) return;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            officialObservationTasks[id] = completion.Task;
            _ = ObserveOfficialObservationAsync(id, () => RecordOfficialObservationAsync(
                observation, sessionSnapshot, talkSnapshot, resolvedSnapshot, language,
                officialObservationShutdown.Token), completion);
        }
    }

    private async Task ObserveOfficialObservationAsync(long id, Func<Task> operation,
        TaskCompletionSource completion)
    {
        try
        {
            // Yield after registry publication so a synchronously completing
            // operation cannot remove itself before its task is registered.
            await Task.Yield();
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (officialObservationShutdown.IsCancellationRequested) { }
        catch (Exception error) { log.Warning(error, "Official voice observation persistence failed"); }
        finally
        {
            completion.TrySetResult();
            lock (officialObservationGate) officialObservationTasks.Remove(id);
        }
    }

    private async Task WaitForOfficialObservationShutdownAsync()
    {
        try { officialObservationShutdown.Cancel(throwOnFirstException: false); }
        catch (ObjectDisposedException) { }
        while (true)
        {
            Task[] tasks;
            lock (officialObservationGate) tasks = officialObservationTasks.Values.ToArray();
            if (tasks.Length == 0) return;
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task RecordOfficialObservationAsync(
        OfficialVoiceClipObservation observation, CutsceneSession sessionSnapshot, ActualTalkLine talkSnapshot,
        ResolvedSpeaker resolvedSnapshot, string language, CancellationToken token)
    {
        // Once the immutable callback snapshot is accepted, metadata durability
        // is independent of line/session invalidation.  Only coordinator
        // shutdown may cancel this operation; invalidation still prevents
        // playback and synthesis through their separate paths.
        await eventGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                LogVoiceLearning("Clip rejected Reason=disposed Path={Path} Sound={Sound}",
                    observation.ScdPath, observation.SoundNumber);
                return;
            }
            var observationAge = observation.StartedAt - talkSnapshot.ObservedAt;
            if (observationAge < TimeSpan.FromMilliseconds(-250) || observationAge > TimeSpan.FromSeconds(1))
            {
                LogVoiceLearning("Clip rejected Reason=current-talk-mismatch Epoch={Epoch} TalkSerial={TalkSerial} AgeSeconds={AgeSeconds:F3} Path={Path} Sound={Sound}",
                    sessionSnapshot.Epoch, talkSnapshot.Serial, observationAge.TotalSeconds,
                    observation.ScdPath, observation.SoundNumber);
                return;
            }
            var resolved = resolvedSnapshot;
            var group = officialVoiceCatalog.Resolve(resolved.NpcBaseId, resolved.DisplayName, language);
            if (resolved.SceneLocal && group is null)
            {
                LogVoiceLearning("Clip rejected Reason=scene-local-identity-unresolved Epoch={Epoch} TalkSerial={TalkSerial} Path={Path} Sound={Sound}",
                    sessionSnapshot.Epoch, talkSnapshot.Serial, observation.ScdPath, observation.SoundNumber);
                return;
            }
            if (group is not null && resolved.SceneLocal)
                resolved = CanonicalizeOfficialAlias(resolved, group);
            LogVoiceLearning("Speaker resolved Epoch={Epoch} TalkSerial={TalkSerial} StableKey={StableKey} NpcBaseId={NpcBaseId} DisplayName={DisplayName}",
                sessionSnapshot.Epoch, talkSnapshot.Serial, resolved.StableKey,
                resolved.NpcBaseId?.ToString() ?? "none", resolved.DisplayName);
            var speaker = await voices.ResolveSpeakerAsync(
                resolved.StableKey, resolved.NpcBaseId, resolved.DisplayName,
                sessionSnapshot.TerritoryId, language, resolved.Metadata, token).ConfigureAwait(false);
            speakerKeys[speaker.Id] = speaker.StableKey;
            var builder = officialReferences;
            if (builder is null)
            {
                LogVoiceLearning("Official learning skipped Reason=builder-unavailable SpeakerId={SpeakerId} StableKey={StableKey} Language={Language}",
                    speaker.Id, speaker.StableKey, language);
                return;
            }
            var officialBuilder = builder;
            var observedTalk = talkSnapshot;
            var target = group is null
                ? speaker
                : await voices.ResolveSpeakerAsync(OfficialVoiceCatalog.CanonicalSpeakerKey(group.Id), null,
                    group.Label, sessionSnapshot.TerritoryId, language, token).ConfigureAwait(false);
            speakerKeys[target.Id] = target.StableKey;
            LogVoiceLearning("Learning identity target SpeakerId={SpeakerId} StableKey={StableKey} Group={Group} Language={Language}",
                target.Id, target.StableKey, group?.Id ?? "unmatched", language);

            async Task LogLearningProgressAsync()
            {
                if (!configuration.VoiceLearningDiagnostics) return;
                try
                {
                    var observedSeconds = await CapturedSecondsForSpeakerAsync(target.Id, language, token)
                        .ConfigureAwait(false);
                    var state = observedSeconds >= OfficialReferenceBuilder.RequiredSeconds
                        ? "build-pending"
                        : "under-threshold";
                    LogVoiceLearning("Learning pending State={State} SpeakerId={SpeakerId} StableKey={StableKey} Language={Language} ObservedSeconds={ObservedSeconds:F3} RequiredSeconds={RequiredSeconds:F3}",
                        state, target.Id, target.StableKey, language, observedSeconds, OfficialReferenceBuilder.RequiredSeconds);
                }
                catch (Exception error)
                {
                    if (configuration.VoiceLearningDiagnostics)
                        log.Debug(error, "Voice learning diagnostic status lookup failed for SpeakerId={SpeakerId}", target.Id);
                }
            }
            var result = await officialBuilder.ObserveAsync(target.Id, observation.ScdPath, observation.SoundNumber,
                observedTalk.Text, language, token).ConfigureAwait(false);
            // Persist the path-bearing official source before the auxiliary
            // hashed native-observation row.  If the latter fails, the SCD
            // path/sound/transcript remains durable and can still be retried
            // safely during the next idle pass.
            await nativeVoices.RecordAsync(speaker.Id, observation.ScdPath, observation.SoundNumber, talkSnapshot.Text,
                token).ConfigureAwait(false);
            if (result.Status == OfficialReferenceObservationStatus.Duplicate)
            {
                LogVoiceLearning("Clip duplicate Source=official SpeakerId={SpeakerId} StableKey={StableKey} Language={Language} Path={Path} Sound={Sound}",
                    target.Id, target.StableKey, language, observation.ScdPath, observation.SoundNumber);
                return;
            }
            var state = result.Status == OfficialReferenceObservationStatus.Pending ? "queued" : "stored";
            LogVoiceLearning("Clip {State} Source=official SpeakerId={SpeakerId} StableKey={StableKey} Language={Language} Path={Path} Sound={Sound} DurationSeconds={DurationSeconds:F3}",
                state, target.Id, target.StableKey, language, observation.ScdPath, observation.SoundNumber,
                result.DurationSeconds);
            await LogLearningProgressAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { log.Warning(error, "Official voice observation persistence failed"); }
        finally { eventGate.Release(); }
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

    private void OnOfficialProfileBuilt(long speakerId, StoredVoiceProfile profile)
    {
        var stableKey = speakerKeys.TryGetValue(speakerId, out var knownStableKey)
            ? knownStableKey
            : "unknown";
        LogVoiceLearning("ProfileBuilt SpeakerId={SpeakerId} StableKey={StableKey} Language={Language} ProfileId={ProfileId} Kind={Kind} ModelHash={ModelHash}",
            speakerId, stableKey, profile.Language, profile.Id, profile.Kind, profile.ModelHash);
        if (speakerKeys.TryGetValue(speakerId, out var profileStableKey))
        {
            profileCache[ProfileCacheKey(profileStableKey, profile.Language)] = profile;
            QueueProfileUpgradeNotification(profileStableKey, profile.Id);
        }
        ScheduleDebugBaseVoiceRefresh(profile.Language);
    }

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
        InvalidateOfficialObservationSnapshots();
        autoAdvanceDiagnostics.Reset();
        Interlocked.Exchange(ref pendingNativeVoice, null);
        Interlocked.Exchange(ref recentOfficialClip, null);
        lock (pendingOfficialClipGate) pendingOfficialClips.Clear();
        Interlocked.Exchange(ref pendingAutoAdvance, null);
        CancelAutoAdvanceRetry();
        Volatile.Write(ref autoAdvanceDispatching, 0);
        audio?.Stop();
        lipSync.Stop(avoidFrameworkDispatch);
        IsSpeaking = false;
        if (session is { } current) scheduler?.InvalidateEpoch(current.Epoch);
        var previousEpoch = session?.Epoch;
        session?.Dispose();
        session = null;
        if (previousEpoch is { } epoch)
            foreach (var key in lineSpeakerSnapshots.Keys.Where(key => key.Epoch == epoch).ToArray())
                lineSpeakerSnapshots.TryRemove(key, out _);
        prefetcher.EndSession();
    }

    private void InvalidateOfficialObservationSnapshots()
    {
        CancellationTokenSource previous;
        lock (officialObservationInvalidationGate)
        {
            previous = currentOfficialObservationCancellation;
            currentOfficialObservationCancellation = new CancellationTokenSource();
            retiredOfficialObservationCancellations.Add(previous);
        }
        try { previous.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void DisposeOfficialObservationInvalidations()
    {
        lock (officialObservationInvalidationGate)
        {
            currentOfficialObservationCancellation.Dispose();
            foreach (var cancellation in retiredOfficialObservationCancellations)
                cancellation.Dispose();
            retiredOfficialObservationCancellations.Clear();
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
            InvalidateBaseHotLoadSafetyOnFramework();
            CancelOfficialReferenceBuild();
            await PauseOfficialPreparationsAsync().ConfigureAwait(false);
            domainPool?.Pause();
            await WaitForOfficialReferenceBuildIdleAsync(linked.Token).ConfigureAwait(false);
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
                    if (Volatile.Read(ref disposed) == 0
                        && !officialObservationShutdown.IsCancellationRequested)
                    {
                        var state = await CaptureFrameworkStateAsync(officialObservationShutdown.Token)
                            .ConfigureAwait(false);
                        domainPool?.ActivateTerritory(state.TerritoryPlaceName);
                    }
                }
                catch (Exception error) when (!officialObservationShutdown.IsCancellationRequested)
                {
                    log.Warning(error, "Failed to restore casting-domain activation after exclusive operation");
                }
                Volatile.Write(ref exclusiveOperationActive, 0);
                if (Volatile.Read(ref disposed) == 0
                    && !officialObservationShutdown.IsCancellationRequested)
                {
                    try { await CaptureFrameworkStateAsync(officialObservationShutdown.Token).ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        log.Warning(error, "Failed to recompute Base hot-load safety after exclusive operation");
                    }
                }
                RequestBaseHotLoadRestore();
                ResumeOfficialPreparations();
                ScheduleOfficialReferenceBuild();
                debugGate.Release();
            }
            lock (coordinatorTaskGate) coordinatorTasks.Remove(completion.Task);
            linked?.Dispose();
        }
    }

    public CastingPoolSnapshot? GetCastingPoolSnapshot() => domainPool?.Snapshot;

    public DebugInferenceSnapshot GetDebugInferenceSnapshot(string language)
    {
        var normalizedLanguage = NormalizeLanguage(language);
        var manager = runtimeManager;
        var designer = voiceDesigner;
        var selected = manager?.Selection?.Effective;
        var normalPlayback = IsSpeaking && Volatile.Read(ref debugPlaybackActive) == 0;
        var ready = bootstrap.State == BootstrapState.Ready
                    && manager is not null && selected is not null && designer is not null && audio is not null
                    && manager.IsReady && designer.IsReady
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
                        ? "Base and VoiceDesign initialized"
                        : "Waiting for Base, VoiceDesign, runtime, and audio initialization";
        var cachedLanguage = Volatile.Read(ref debugVoiceLanguage);
        if (!String.Equals(cachedLanguage, normalizedLanguage, StringComparison.Ordinal))
            readiness += "; refresh Base voices for this language";
        return new(
            ready && !cutscenes.IsInCutscene && !condition[ConditionFlag.InCombat],
            Volatile.Read(ref debugRunning) != 0 || Volatile.Read(ref debugPlaybackActive) != 0,
            readiness,
            selected is null ? "Preparing..." : $"{selected.Description} [{selected.Name}]",
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
                OfficialVoiceCatalog.CanonicalSpeakerKey(group.Id), normalizedLanguage, token).ConfigureAwait(false);
            if (profile is { Kind: VoiceProfileKind.Official })
                debugBaseProfiles[group.Id] = profile;
            var hasSources = group.Sources.TryGetValue(normalizedLanguage, out var sources) && sources.Count > 0;
            var ready = debugBaseProfiles.ContainsKey(group.Id);
            var capturedSeconds = ready ? 0 : await CapturedSecondsAsync(group.Id, normalizedLanguage, token)
                .ConfigureAwait(false);
            options.Add(new(group.Id, group.Label, ready,
                ready
                    ? "Ready"
                    : hasSources
                        ? "Curated source — build pending"
                        : capturedSeconds > 0
                            ? $"Captured {capturedSeconds:F1} / {OfficialReferenceBuilder.RequiredSeconds:F1} s"
                            : "No verified source"));
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

    private Task<double> CapturedSecondsAsync(string groupId, string language, CancellationToken token) =>
        database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(SUM(r.duration_seconds),0)
                FROM official_reference_clip r JOIN speaker s ON s.id=r.speaker_id
                WHERE s.stable_key=$speaker AND r.language=$language AND r.source_origin='observed'
                """;
            command.Parameters.AddWithValue("$speaker", OfficialVoiceCatalog.CanonicalSpeakerKey(groupId));
            command.Parameters.AddWithValue("$language", language);
            return Convert.ToDouble(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
        }, token);

    private Task<double> CapturedSecondsForSpeakerAsync(long speakerId, string language, CancellationToken token) =>
        database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(SUM(duration_seconds),0)
                FROM official_reference_clip
                WHERE speaker_id=$speaker AND language=$language AND source_origin='observed'
                """;
            command.Parameters.AddWithValue("$speaker", speakerId);
            command.Parameters.AddWithValue("$language", language);
            return Convert.ToDouble(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
        }, token);

    private Task StartOfficialGroupPreparation(
        long speakerId, ResolvedSpeaker speaker, string language, bool allowBuild,
        CancellationToken token)
    {
        var group = officialVoiceCatalog.Resolve(speaker.NpcBaseId, speaker.DisplayName, language);
        if (group is null) return Task.CompletedTask;
        return PrepareAndAttachAsync(group);

        async Task PrepareAndAttachAsync(OfficialVoiceGroup officialGroup)
        {
            // This lookup is intentionally awaited before line synthesis.  It
            // is cheap and lets an already-built same-language official clone
            // win the first line without racing a new design assignment.
            var profile = await EnsureOfficialGroupProfileAsync(
                officialGroup, language, token, allowBuild: false)
                .ConfigureAwait(false);
            if (profile is not null)
            {
                await AttachOfficialProfileAsync(speakerId, profile, language, token).ConfigureAwait(false);
                return;
            }
            if (allowBuild) QueueOfficialGroupPreparation(speakerId, officialGroup, language);
        }
    }

    private void QueueOfficialGroupPreparation(long speakerId, OfficialVoiceGroup group, string language)
    {
        var key = $"{group.Id}\0{NormalizeLanguage(language)}";
        CancellationToken token;
        lock (officialPreparationGate)
        {
            if (Volatile.Read(ref disposed) != 0 || officialPreparationPaused
                || officialPreparationTasks.ContainsKey(key)) return;
            token = officialPreparationCancellation.Token;
            var task = PrepareOfficialGroupWhenIdleAsync(speakerId, group, language, key, token);
            officialPreparationTasks[key] = task;
        }
    }

    private async Task PrepareOfficialGroupWhenIdleAsync(
        long speakerId, OfficialVoiceGroup group, string language, string key, CancellationToken token)
    {
        try
        {
            await Task.Yield();
            var delay = TimeSpan.FromSeconds(1);
            while (Volatile.Read(ref disposed) == 0 && !token.IsCancellationRequested)
            {
                var state = await CaptureFrameworkStateAsync(token).ConfigureAwait(false);
                if (state.CanProcessOfficialReferences)
                {
                    try
                    {
                        var profile = await EnsureOfficialGroupProfileAsync(group, language, token, true)
                            .ConfigureAwait(false);
                        if (profile is not null)
                        {
                            await AttachOfficialProfileAsync(speakerId, profile, language, token)
                                .ConfigureAwait(false);
                            return;
                        }
                        if (!group.Sources.TryGetValue(language, out var sources) || sources.Count == 0)
                        {
                            ScheduleOfficialReferenceBuild();
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        log.Warning(error, "Curated official preparation failed for {Group}/{Language}; retrying",
                            group.Id, language);
                    }
                }
                await Task.Delay(delay, token).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            log.Warning(error, "Curated official preparation failed for {Group}/{Language}; retrying when idle",
                group.Id, language);
            // Keep the durable curated source available to the normal pending
            // processor.  A later normal line can enqueue this group again.
        }
        finally
        {
            lock (officialPreparationGate)
            {
                officialPreparationTasks.Remove(key);
            }
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
            profileCache[ProfileCacheKey(stableKey, language)] = profile;
            QueueProfileUpgradeNotification(stableKey, profile.Id);
        }
    }

    private async Task<StoredVoiceProfile?> EnsureOfficialGroupProfileAsync(
        OfficialVoiceGroup group,
        string language,
        CancellationToken token,
        bool allowBuild = true)
    {
        language = NormalizeLanguage(language);
        var canonicalKey = OfficialVoiceCatalog.CanonicalSpeakerKey(group.Id);
        var current = await voices.GetBestVoiceByStableKeyAsync(canonicalKey, language, token).ConfigureAwait(false);
        if (current is { Kind: VoiceProfileKind.Official }) return current;
        if (!allowBuild) return null;
        if (officialReferences is not { } builder) return null;
        var canonical = await voices.ResolveSpeakerAsync(
            canonicalKey, null, group.Label, 0, language, token).ConfigureAwait(false);
        speakerKeys[canonical.Id] = canonical.StableKey;
        if (group.Sources.TryGetValue(language, out var sources) && sources.Count > 0)
        {
            foreach (var source in sources.OrderByDescending(value => value.Preferred))
            {
                try
                {
                    await builder.AddCuratedAsync(canonical.Id, source, language, officialVoiceCatalog.Version, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    log.Warning(error, "Curated official voice source is unavailable for {Group}/{Language}",
                        group.Id, language);
                    continue;
                }
                current = await voices.GetBestVoiceAsync(canonical.Id, language, token).ConfigureAwait(false);
                if (current is { Kind: VoiceProfileKind.Official }) return current;
            }
        }
        current = await builder.RebuildPersistedAsync(canonical.Id, language, token).ConfigureAwait(false) ?? current;
        return current is { Kind: VoiceProfileKind.Official } ? current : null;
    }

    public Task RunVoiceDesignDebugAsync(
        string text,
        string instruction,
        string language,
        CancellationToken token) => RunDebugAsync("VoiceDesign", text, language, token, async (line, activeToken) =>
    {
        var designer = voiceDesigner ?? throw new InvalidOperationException("VoiceDesign is not initialized");
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
        await RunDebugAsync("Base", text, normalizedLanguage, token, async (line, activeToken) =>
        {
            if (!debugBaseProfiles.TryGetValue(presetKey, out var profile))
            {
                var group = officialVoiceCatalog.Groups.SingleOrDefault(value => value.Id == presetKey)
                            ?? throw new InvalidOperationException("The selected official voice group does not exist");
                // Debug inference must never start curated extraction while
                // the debug gate owns the selected device.  Normal dialogue
                // queues that work for safe idle; debug consumes only a
                // profile already attached to the selected language.
                profile = await EnsureOfficialGroupProfileAsync(
                              group, normalizedLanguage, activeToken, allowBuild: false)
                              .ConfigureAwait(false)
                          ?? throw new InvalidOperationException("The selected character has less than 10 seconds of verified official voice material");
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

    private Task RunDebugAsync(
        string kind,
        string text,
        string language,
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
        return RunTrackedDebugAsync(kind, text, language, token, synthesize, completion);
    }

    private async Task RunTrackedDebugAsync(
        string kind,
        string text,
        string language,
        CancellationToken token,
        Func<DubLine, CancellationToken, Task> synthesize,
        TaskCompletionSource completion)
    {
        try
        {
            await RunDebugCoreAsync(kind, text, language, token, synthesize).ConfigureAwait(false);
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
            InvalidateBaseHotLoadSafetyOnFramework();
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(SessionCoordinator));
            snapshot = await CaptureDebugInferenceSnapshotAsync(language, operationToken).ConfigureAwait(false);
            if (!snapshot.Ready) throw new InvalidOperationException(snapshot.Readiness);
            cancellation = coordinatorCancellation;
            lock (debugCancellationGate) debugCancellation = cancellation;
            CancelOfficialReferenceBuild();
            await PauseOfficialPreparationsAsync().ConfigureAwait(false);
            domainPool?.Pause();
            await WaitForOfficialReferenceBuildIdleAsync(cancellation.Token).ConfigureAwait(false);
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
                PlaybackDeadline = DateTimeOffset.MaxValue,
            };
            line.TryTransition(DubLineState.Generating, DubLineState.Predicted);
            await DispatchFrameworkActionAsync(() => audio!.Play(line, configuration.Volume),
                cancellation.Token).ConfigureAwait(false);
            await synthesize(line, cancellation.Token).ConfigureAwait(false);
            Volatile.Write(ref debugStatus, $"{kind}: synthesis passed; playback active");
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
            }
            Volatile.Write(ref debugRunning, 0);
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
                RequestBaseHotLoadRestore();
            ResumeOfficialPreparations();
            ScheduleOfficialReferenceBuild();
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

        BestEffortSync("Initial session cancellation", () => CancelSession(avoidFrameworkDispatch: true));
        BestEffortSync("Debug inference cancellation", () => CancelDebugInference(disposingFromFrameworkThread));
        BestEffortSync("Official preparation cancellation", CancelOfficialPreparations);
        try { officialObservationShutdown.Cancel(throwOnFirstException: false); }
        catch (Exception error) { Record("Official observation cancellation", error); }
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
        if (scheduler is not null) scheduler.BecameIdle -= OnSchedulerIdle;
        nativeVoice.TalkVoiceStarted -= OnNativeVoiceStarted;
        nativeVoice.OfficialVoiceClipObserved -= OnOfficialVoiceClipObserved;
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
        await BestEffortAsync("Native VO processing", WaitForNativeVoiceProcessingAsync);
        await BestEffortAsync("Official observation", WaitForOfficialObservationShutdownAsync);
        BestEffortSync("Official observation invalidation", DisposeOfficialObservationInvalidations);
        await BestEffortAsync("Auto-advance retry", StopAutoAdvanceRetryAsync);
        await BestEffortAsync("Official preparation", WaitForOfficialPreparationShutdownAsync);
        CancellationTokenSource[] preparationCancellations;
        lock (officialPreparationGate)
        {
            preparationCancellations = [
                officialPreparationCancellation,
                .. retiredOfficialPreparationCancellations,
            ];
            retiredOfficialPreparationCancellations.Clear();
        }
        foreach (var cancellation in preparationCancellations)
            BestEffortSync("Official preparation cancellation source", cancellation.Dispose);
        await BestEffortAsync("Debug Base-voice refresh", WaitForDebugBaseVoiceRefreshShutdownAsync);
        BestEffortSync("Official reference cancellation", CancelOfficialReferenceBuild);
        await BestEffortAsync("Official reference build", WaitForOfficialReferenceShutdownAsync);
        BestEffortSync("Official reference event", () =>
        {
            if (officialReferences is not null) officialReferences.ProfileBuilt -= OnOfficialProfileBuilt;
        });
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
        var officialReferencesToDispose = officialReferences;
        if (officialReferencesToDispose is not null)
            await BestEffortAsync("Official reference builder", () => officialReferencesToDispose.DisposeAsync().AsTask());
        var voiceDesignerToDispose = voiceDesigner;
        if (voiceDesignerToDispose is not null)
            await BestEffortAsync("VoiceDesign runtime", () => voiceDesignerToDispose.DisposeAsync().AsTask());
        BestEffortSync("Audio engine", () => audio?.Dispose());
        await BestEffortAsync("Lip-sync service", () => lipSync.DisposeAsync(disposingFromFrameworkThread).AsTask());
        BestEffortSync("Debug gate", debugGate.Dispose);
        BestEffortSync("Event gate", eventGate.Dispose);
        BestEffortSync("Official observation shutdown", officialObservationShutdown.Dispose);

        if (failures.Count > 0)
            throw new AggregateException("Session coordinator teardown failed", failures);
    }
}
