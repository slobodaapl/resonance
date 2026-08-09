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

    private const string DebugLineSource = "Resonance inference debug";
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
    private AudioEngine? audio;
    private DubScheduler? scheduler;
    private VoiceDesigner? voiceDesigner;
    private Task? voiceDesignerInitialization;
    private RuntimeManager? runtimeManager;
    private CastingDomainPool? domainPool;
    private CutsceneSession? session;
    private long nextEpoch;
    private int disposed;
    private NativeVoiceObservation? pendingNativeVoice;
    private PendingAutoAdvance? pendingAutoAdvance;
    private readonly ConcurrentDictionary<string, StoredVoiceProfile> profileCache = new();
    private readonly ConcurrentDictionary<long, string> speakerKeys = new();
    private OfficialReferenceBuilder? officialReferences;
    private IReadOnlyList<DebugBaseVoiceOption> debugBaseVoices = [];
    private readonly ConcurrentDictionary<string, StoredVoiceProfile> debugBaseProfiles = new(StringComparer.Ordinal);
    private string? debugVoiceLanguage;
    private string debugStatus = "Idle";
    private CancellationTokenSource? debugCancellation;
    private long nextDebugSequence;
    private int debugRunning;
    private int debugPlaybackActive;

    public bool IsSpeaking { get; private set; }
    public event Action<DubLine>? LineStarted;
    public event Action<DubLine>? LineFinished;
    public event Action<NativeVoiceObservation>? NativeVoiceObserved;
    public event Action<string, string>? SpeakerProfileUpgraded;
    public string? GetSpeakerProfile(string stableKey)
    {
        var language = CurrentLanguage();
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
        talk.Advanced += OnTalkClosed;
        talk.Hidden += OnTalkClosed;
        talk.Finalized += OnTalkClosed;
        client.TerritoryChanged += OnTerritoryChanged;
        client.Logout += OnLogout;
        bootstrap.Ready += OnRuntimeReady;
        bootstrap.VoiceDesignReady += OnVoiceDesignReady;
        nativeVoice.TalkVoiceStarted += OnNativeVoiceStarted;
        nativeVoice.OfficialVoiceClipObserved += OnOfficialVoiceClipObserved;
        if (cutscenes.IsInCutscene) OnCutsceneStarted();
    }

    private void OnRuntimeReady(RuntimeManager manager)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        runtimeManager = manager;
        manager.SelectionChanged += OnBackendSelectionChanged;
        audio ??= new AudioEngine(configuration.AudioOutputDeviceNumber);
        audio.Started += lipSync.Start;
        audio.Finished += _ => lipSync.Stop();
        audio.Started += line => { IsSpeaking = true; LineStarted?.Invoke(line); };
        audio.Finished += line => { IsSpeaking = false; LineFinished?.Invoke(line); };
        audio.Finished += OnAudioFinished;
        audio.Started += line =>
        {
            if (line.SourceQuest == DebugLineSource) Volatile.Write(ref debugPlaybackActive, 1);
        };
        audio.Finished += line =>
        {
            if (line.SourceQuest != DebugLineSource) return;
            Volatile.Write(ref debugPlaybackActive, 0);
            if (line.State == DubLineState.Completed) Volatile.Write(ref debugStatus, $"{line.SpeakerName}: playback passed");
            line.Dispose();
        };
        scheduler = new DubScheduler(manager.Runtime, ResolveVoiceAsync, lineCache, manager.ModelHash, CurrentLanguage());
        officialReferences = new OfficialReferenceBuilder(
            database, voices, manager.Runtime, scdExtractor, officialWorkingDirectory, manager.ModelHash);
        officialReferences.ProfileBuilt += OnOfficialProfileBuilt;
        _ = officialReferences.ProcessPendingAsync(CurrentLanguage(), CancellationToken.None).ContinueWith(
            task => log.Warning(task.Exception!.GetBaseException(), "Pending official voice processing failed"),
            TaskContinuationOptions.OnlyOnFaulted);
        scheduler.LineBuffered += OnLineBuffered;
        scheduler.PredictionStreamable += _ => TryCompleteAutoAdvance();
        scheduler.LineFailed += (line, error) => log.Error(error, "Synthesis failed for line {Serial}", line.Sequence);
    }

    private void OnVoiceDesignReady(string designPath, string codecPath)
    {
        var manager = bootstrap.RuntimeManager;
        var backend = manager?.Selection?.Effective.Name;
        if (manager is null || backend is null || Volatile.Read(ref disposed) != 0) return;
        voiceDesignerInitialization = Task.Run(async () =>
        {
            VoiceDesigner? created = null;
            try
            {
                created = new VoiceDesigner(manager.Runtime, designPath, codecPath, backend);
                var latestBackend = manager.Selection?.Effective.Name;
                if (latestBackend is not null && latestBackend != backend)
                    await created.SwitchBackendAsync(latestBackend, CancellationToken.None).ConfigureAwait(false);
                if (Volatile.Read(ref disposed) != 0)
                {
                    await created.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                voiceDesigner = created;
                await RefreshDebugBaseVoicesAsync(CurrentLanguage(), CancellationToken.None).ConfigureAwait(false);
                if (domainPool is null)
                {
                    var pool = new CastingDomainPool(
                        voices,
                        catalog,
                        () => voiceDesigner,
                        () => !cutscenes.IsInCutscene
                              && !condition[ConditionFlag.InCombat] && scheduler?.HasUrgentWork != true,
                        () => territoryPlaceName(client.TerritoryType),
                        CurrentLanguage,
                        () => (configuration.ReadyMasculineVoices, configuration.ReadyFeminineVoices),
                        manager.ModelHash,
                        configuration.GetPromptOverride,
                        () => configuration.BackgroundCasting);
                    pool.Failed += error => log.Warning(error, "Background voice casting failed; retrying");
                    domainPool = pool;
                    pool.ActivateTerritory(territoryPlaceName(client.TerritoryType));
                }
            }
            catch (Exception error)
            {
                if (created is not null && !ReferenceEquals(voiceDesigner, created))
                    await created.DisposeAsync().ConfigureAwait(false);
                log.Error(error, "VoiceDesign runtime initialization failed");
            }
        });
    }

    private void OnBackendSelectionChanged(BackendSelection selection)
    {
        var designer = voiceDesigner;
        if (designer is null || Volatile.Read(ref disposed) != 0) return;
        _ = designer.SwitchBackendAsync(selection.Effective.Name, CancellationToken.None).ContinueWith(
            task => log.Error(task.Exception!.GetBaseException(), "VoiceDesign backend migration failed"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private void OnCutsceneStarted()
    {
        domainPool?.Pause();
        domainPool?.ActivateTerritory(territoryPlaceName(client.TerritoryType));
        CancelSession();
        session = new CutsceneSession(Interlocked.Increment(ref nextEpoch), client.TerritoryType);
        prefetcher.BeginSession();
    }

    private void OnCutsceneEnded()
    {
        domainPool?.Pause();
        CancelSession();
    }
    private void OnTerritoryChanged(uint territory)
    {
        domainPool?.Pause();
        domainPool?.ActivateTerritory(territoryPlaceName(territory));
        CancelSession();
    }
    private void OnLogout(int _, int __)
    {
        domainPool?.Pause();
        CancelSession();
    }

    private void OnLineChanged(ActualTalkLine line)
    {
        if (!configuration.Enabled || !nativeVoice.IsAvailable || !cutscenes.IsInCutscene || session is null || scheduler is null) return;
        audio?.Stop();
        lipSync.Stop();
        CancelActualLines();
        _ = HandleLineAsync(line);
    }

    private async Task HandleLineAsync(ActualTalkLine actual)
    {
        await eventGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0) return;
            var current = session;
            var currentScheduler = scheduler;
            if (current is null || currentScheduler is null || !cutscenes.IsInCutscene) return;
            var resolved = speakers.Resolve(actual, current.Epoch);
            var language = CurrentLanguage();
            var stored = await voices.ResolveSpeakerAsync(
                resolved.StableKey, resolved.NpcBaseId, resolved.DisplayName,
                current.TerritoryId, language, resolved.Metadata, CancellationToken.None).ConfigureAwait(false);
            if (!IsCurrent(current)) return;
            speakerKeys[stored.Id] = stored.StableKey;
            StartOfficialGroupPreparation(stored.Id, resolved, language);
            var casting = await ResolveCastingAsync(stored, resolved, current.TerritoryId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!IsCurrent(current)) return;
            domainPool?.ActivateResolution(casting);
            var slot = catalog.SelectBestSlot(casting, resolved.Evidence);
            var assignment = new ResolvedLineSpeaker(
                stored.StableKey,
                stored.DisplayName,
                stored.Id,
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
                line = current.AddActual(stored.StableKey, stored.DisplayName, actual.Text, language);
                line.ApplyResolvedSpeaker(assignment);
            }
            else
            {
                currentScheduler.PromoteActual(current.Epoch, line.Sequence, DateTimeOffset.UtcNow);
            }
            line.ActualTalkSerial = actual.Serial;
            var pendingNative = Interlocked.Exchange(ref pendingNativeVoice, null);
            if (pendingNative is not null && actual.ObservedAt - pendingNative.StartedAt <= TimeSpan.FromSeconds(1))
            {
                line.NativeVoiceStatus = NativeVoiceStatus.NativeVoiced;
                line.State = DubLineState.NativeVoiced;
            }
            else
            {
                if (promotedPrediction && line.State == DubLineState.Buffered && line.Audio.ProducerCompleted)
                    OnLineBuffered(line);
                else if (!promotedPrediction || line.State == DubLineState.Predicted)
                    currentScheduler.Enqueue(line);
            }

            var update = prefetcher.Observe(actual.Speaker, actual.Text);
            if (update.Synchronized)
            {
                var predicted = current.ReplacePredictions(update.Future.Select(value =>
                    ($"predicted:{value.Speaker}", value.Speaker, value.Text)), language);
                foreach (var future in predicted)
                {
                    var syntheticActual = new ActualTalkLine(future.Sequence, future.SpeakerName, future.Text, DateTimeOffset.UtcNow);
                    var futureResolved = speakers.Resolve(syntheticActual, current.Epoch);
                    if (!IsCurrent(current))
                    {
                        future.Cancel(DubLineState.Invalidated);
                        return;
                    }
                    future.SpeakerKey = futureResolved.StableKey;
                    future.SpeakerName = futureResolved.DisplayName;
                    future.SpeakerId = null;
                    future.VoiceArchetype = futureResolved.Archetype;
                    future.VoiceSex = futureResolved.Sex;
                    future.ActorAddress = futureResolved.ActorAddress;
                    future.CastingEvidence = futureResolved.Evidence;
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

    private async ValueTask<VoiceReference?> ResolveVoiceAsync(DubLine line, CancellationToken token)
    {
        var language = line.Language
            ?? throw new InvalidOperationException("Queued line has no resolved dubbing language");
        if (line.ActualStatus == ActualStatus.Predicted)
        {
            var predictedProfile = profileCache.TryGetValue(ProfileCacheKey(line.SpeakerKey, language), out var cached)
                ? cached
                : await voices.GetBestVoiceByStableKeyAsync(line.SpeakerKey, language, token).ConfigureAwait(false);
            if (predictedProfile is null)
                throw new InvalidOperationException("Predicted speaker has no persistent voice assignment");
            line.VoiceProfileId = predictedProfile.Id;
            line.VoiceProfileHash = predictedProfile.ProfileHash;
            profileCache[ProfileCacheKey(line.SpeakerKey, language)] = predictedProfile;
            return predictedProfile.Reference;
        }
        if (line.SpeakerId is not { } speakerId) return null;
        var stored = await voices.GetBestVoiceAsync(speakerId, language, token).ConfigureAwait(false);
        if (stored is not null)
        {
            line.VoiceProfileId = stored.Id;
            line.VoiceProfileHash = stored.ProfileHash;
            profileCache[ProfileCacheKey(line.SpeakerKey, language)] = stored;
            return stored.Reference;
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
            SpeakerProfileUpgraded?.Invoke(line.SpeakerKey, pooled.Id);
            return pooled.Reference;
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
        var reference = line.ActualStatus == ActualStatus.Actual
            ? await designer.SynthesizeDesignedLineAsync(line.Text, instruction, seed, language, line.Audio, token).ConfigureAwait(false)
            : await designer.DesignReferenceAsync(instruction, seed, language, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (line.ActualStatus == ActualStatus.Actual) line.DirectSynthesisCompleted = true;
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
        SpeakerProfileUpgraded?.Invoke(line.SpeakerKey, profile.Id);
        return reference;
    }

    private async Task<CastingResolution> ResolveCastingAsync(
        SpeakerIdentity speaker,
        ResolvedSpeaker resolved,
        uint territoryId,
        CancellationToken token)
    {
        var firstTerritory = territoryPlaceName(speaker.TerritoryId);
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
                // unusable; resolve new work through the current catalog.
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

    private static string ProfileCacheKey(string stableKey, string language) =>
        $"{stableKey}\0{language}";

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
    }

    private void TryCompleteAutoAdvance()
    {
        var pending = Volatile.Read(ref pendingAutoAdvance);
        var current = session;
        if (pending is null || current is null || current.Epoch != pending.SessionEpoch
            || !configuration.AutoAdvanceDubbedCutsceneDialogue || !cutscenes.IsInCutscene) return;
        if (!AutoAdvancePolicy.IsImmediateNextPredictionPlayable(current.Lines, pending.LineSequence)) return;
        if (!ReferenceEquals(Interlocked.CompareExchange(ref pendingAutoAdvance, null, pending), pending)) return;
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (configuration.AutoAdvanceDubbedCutsceneDialogue && cutscenes.IsInCutscene)
                talk.TryAdvance(pending.TalkSerial, pending.Speaker, pending.Text);
        });
    }

    private void OnTalkClosed(ActualTalkLine? _)
    {
        Interlocked.Exchange(ref pendingAutoAdvance, null);
        audio?.Stop();
        lipSync.Stop();
        CancelActualLines();
    }

    private void CancelActualLines()
    {
        if (session is not { } current) return;
        foreach (var line in current.Lines.Where(line => line.ActualStatus == ActualStatus.Actual && !line.IsTerminal))
            line.Cancel(DubLineState.Invalidated);
    }

    private void OnNativeVoiceStarted(NativeVoiceObservation observation)
    {
        Interlocked.Exchange(ref pendingNativeVoice, observation);
        NativeVoiceObserved?.Invoke(observation);
        var currentLine = talk.Current;
        var currentSession = session;
        if (currentLine is null || currentSession is null) return;
        var candidate = currentSession.Lines
            .Where(line => line.ActualStatus == ActualStatus.Actual && !line.IsTerminal)
            .OrderByDescending(line => line.Sequence)
            .FirstOrDefault();
        if (candidate is null) return;
        Interlocked.Exchange(ref pendingNativeVoice, null);
        candidate.NativeVoiceStatus = NativeVoiceStatus.NativeVoiced;
        scheduler?.NativeVoiceStarted(currentSession.Epoch, candidate.Sequence);
        audio?.Stop();
        lipSync.Stop();
        IsSpeaking = false;
        log.Debug("Native VO suppressed synthetic line {Sequence}: {Path}", candidate.Sequence, observation.ScdPath);
    }

    private void OnOfficialVoiceClipObserved(OfficialVoiceClipObservation observation)
    {
        if (!cutscenes.IsInCutscene || session is not { } current || talk.Current is null) return;
        _ = RecordOfficialObservationAsync(observation, current.Epoch);
    }

    private async Task RecordOfficialObservationAsync(OfficialVoiceClipObservation observation, long sessionEpoch)
    {
        await eventGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0) return;
            var actual = talk.Current;
            var current = session;
            if (actual is null || current is null || current.Epoch != sessionEpoch || !cutscenes.IsInCutscene
                || (observation.StartedAt - actual.ObservedAt).Duration() > TimeSpan.FromSeconds(1)) return;
            var resolved = speakers.Resolve(actual, current.Epoch);
            var speaker = await voices.ResolveSpeakerAsync(
                resolved.StableKey, resolved.NpcBaseId, resolved.DisplayName,
                current.TerritoryId, CurrentLanguage(), resolved.Metadata, CancellationToken.None).ConfigureAwait(false);
            if (!IsCurrent(current)) return;
            speakerKeys[speaker.Id] = speaker.StableKey;
            await nativeVoices.RecordAsync(speaker.Id, observation.ScdPath, observation.SoundNumber, actual.Text,
                CancellationToken.None).ConfigureAwait(false);
            var builder = officialReferences;
            if (builder is not null)
            {
                var group = officialVoiceCatalog.Resolve(resolved.NpcBaseId, resolved.DisplayName, CurrentLanguage());
                var target = group is null
                    ? speaker
                    : await voices.ResolveSpeakerAsync(OfficialVoiceCatalog.CanonicalSpeakerKey(group.Id), null,
                        group.Label, current.TerritoryId, CurrentLanguage(), CancellationToken.None).ConfigureAwait(false);
                speakerKeys[target.Id] = target.StableKey;
                _ = builder.ObserveAsync(target.Id, observation.ScdPath, observation.SoundNumber, actual.Text,
                    CurrentLanguage(), CancellationToken.None).ContinueWith(
                    task => log.Warning(task.Exception!.GetBaseException(), "Official voice learning failed"),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (Exception error) { log.Warning(error, "Official voice observation persistence failed"); }
        finally { eventGate.Release(); }
    }

    private void OnOfficialProfileBuilt(long speakerId, StoredVoiceProfile profile)
    {
        if (speakerKeys.TryGetValue(speakerId, out var stableKey))
        {
            profileCache[ProfileCacheKey(stableKey, profile.Language)] = profile;
            SpeakerProfileUpgraded?.Invoke(stableKey, profile.Id);
        }
        _ = RefreshDebugBaseVoicesAsync(profile.Language, CancellationToken.None).ContinueWith(
            task => log.Warning(task.Exception!.GetBaseException(), "Debug Base-voice refresh failed"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private bool IsCurrent(CutsceneSession candidate) =>
        Volatile.Read(ref disposed) == 0 && cutscenes.IsInCutscene && ReferenceEquals(session, candidate);

    private void CancelSession()
    {
        Interlocked.Exchange(ref pendingNativeVoice, null);
        Interlocked.Exchange(ref pendingAutoAdvance, null);
        audio?.Stop();
        lipSync.Stop();
        IsSpeaking = false;
        if (session is { } current) scheduler?.InvalidateEpoch(current.Epoch);
        session?.Dispose();
        session = null;
        prefetcher.EndSession();
    }

    public Task RegenerateCurrentTerritoryVoicesAsync(CancellationToken token) => domainPool is { } pool
        ? pool.RegenerateCurrentTerritoryAsync(token)
        : Task.FromException(new InvalidOperationException("VoiceDesign and the casting-domain pool are not ready"));

    public Task RegenerateCurrentZoneVoicesAsync(CancellationToken token) =>
        RegenerateCurrentTerritoryVoicesAsync(token);

    public Task RegenerateDomainVoicesAsync(string domainId, CancellationToken token) => domainPool is { } pool
        ? pool.RegenerateDomainAsync(domainId, token)
        : Task.FromException(new InvalidOperationException("VoiceDesign and the casting-domain pool are not ready"));

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
                    && !normalPlayback;
        var readiness = cutscenes.IsInCutscene
            ? "Unavailable during a cutscene"
            : condition[ConditionFlag.InCombat]
                ? "Unavailable during combat"
                : normalPlayback
                    ? "Unavailable while normal Resonance playback is active"
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

    public async Task RefreshDebugBaseVoicesAsync(string language, CancellationToken token)
    {
        var normalizedLanguage = NormalizeLanguage(language);
        var modelHash = runtimeManager?.ModelHash;
        var options = new List<DebugBaseVoiceOption>(officialVoiceCatalog.Groups.Count);
        debugBaseProfiles.Clear();
        foreach (var group in officialVoiceCatalog.Groups)
        {
            var profile = await voices.GetBestVoiceByStableKeyAsync(
                OfficialVoiceCatalog.CanonicalSpeakerKey(group.Id), normalizedLanguage, token).ConfigureAwait(false);
            if (profile is not null && String.Equals(profile.ModelHash, modelHash, StringComparison.Ordinal)
                && profile.Kind == VoiceProfileKind.Official)
                debugBaseProfiles[group.Id] = profile;
            var hasSources = group.Sources.TryGetValue(normalizedLanguage, out var sources) && sources.Count > 0;
            var ready = debugBaseProfiles.ContainsKey(group.Id);
            var capturedSeconds = ready ? 0 : await CapturedSecondsAsync(group.Id, normalizedLanguage, token)
                .ConfigureAwait(false);
            options.Add(new(group.Id, group.Label, ready || hasSources,
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
            if (groupLabels.Contains(captured.DisplayName)
                || !String.Equals(captured.Profile.ModelHash, modelHash, StringComparison.Ordinal)) continue;
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

    private void StartOfficialGroupPreparation(long speakerId, ResolvedSpeaker speaker, string language)
    {
        var group = officialVoiceCatalog.Resolve(speaker.NpcBaseId, speaker.DisplayName, language);
        if (group is null) return;
        _ = PrepareAndAttachAsync().ContinueWith(
            task => log.Warning(task.Exception!.GetBaseException(), "Official voice group preparation failed"),
            TaskContinuationOptions.OnlyOnFaulted);

        async Task PrepareAndAttachAsync()
        {
            var profile = await EnsureOfficialGroupProfileAsync(group, language, CancellationToken.None)
                .ConfigureAwait(false);
            if (profile is null || Volatile.Read(ref disposed) != 0) return;
            profile = await voices.SaveAndAssignAsync(speakerId, profile, CancellationToken.None).ConfigureAwait(false);
            if (speakerKeys.TryGetValue(speakerId, out var stableKey))
            {
                profileCache[ProfileCacheKey(stableKey, language)] = profile;
                SpeakerProfileUpgraded?.Invoke(stableKey, profile.Id);
            }
        }
    }

    private async Task<StoredVoiceProfile?> EnsureOfficialGroupProfileAsync(
        OfficialVoiceGroup group,
        string language,
        CancellationToken token)
    {
        language = NormalizeLanguage(language);
        var canonicalKey = OfficialVoiceCatalog.CanonicalSpeakerKey(group.Id);
        var current = await voices.GetBestVoiceByStableKeyAsync(canonicalKey, language, token).ConfigureAwait(false);
        var modelHash = runtimeManager?.ModelHash;
        if (current is { Kind: VoiceProfileKind.Official }
            && String.Equals(current.ModelHash, modelHash, StringComparison.Ordinal)) return current;
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
                if (current is { Kind: VoiceProfileKind.Official }
                    && String.Equals(current.ModelHash, modelHash, StringComparison.Ordinal)) return current;
            }
        }
        current = await builder.RebuildPersistedAsync(canonical.Id, language, token).ConfigureAwait(false) ?? current;
        return current is { Kind: VoiceProfileKind.Official }
               && String.Equals(current.ModelHash, modelHash, StringComparison.Ordinal)
            ? current
            : null;
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
        await designer.SynthesizeDesignedLineAsync(
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
        if (!debugBaseProfiles.TryGetValue(presetKey, out var profile))
        {
            var group = officialVoiceCatalog.Groups.SingleOrDefault(value => value.Id == presetKey)
                        ?? throw new InvalidOperationException("The selected official voice group does not exist");
            profile = await EnsureOfficialGroupProfileAsync(group, normalizedLanguage, token).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("The selected character has less than 10 seconds of verified official voice material");
            debugBaseProfiles[presetKey] = profile;
            await RefreshDebugBaseVoicesAsync(normalizedLanguage, token).ConfigureAwait(false);
        }
        await RunDebugAsync("Base", text, normalizedLanguage, token, async (line, activeToken) =>
        {
            var manager = runtimeManager ?? throw new InvalidOperationException("Base runtime is not initialized");
            await manager.SynthesizeAsync(
                new(line.Text, line.Language!, profile.Reference, null, 0x4241534554455354L),
                line.Audio,
                activeToken).ConfigureAwait(false);
            line.Audio.Complete();
        }).ConfigureAwait(false);
    }

    public void CancelDebugInference()
    {
        lock (debugCancellationGate) debugCancellation?.Cancel();
        audio?.Stop();
        Volatile.Write(ref debugStatus, "Cancelled");
    }

    private async Task RunDebugAsync(
        string kind,
        string text,
        string language,
        CancellationToken token,
        Func<DubLine, CancellationToken, Task> synthesize)
    {
        if (String.IsNullOrWhiteSpace(text)) throw new ArgumentException("Sample sentence is empty", nameof(text));
        var snapshot = GetDebugInferenceSnapshot(language);
        if (!snapshot.Ready) throw new InvalidOperationException(snapshot.Readiness);
        if (snapshot.Running) throw new InvalidOperationException("A debug inference or playback operation is already active");
        await debugGate.WaitAsync(token).ConfigureAwait(false);
        CancellationTokenSource? cancellation = null;
        DubLine? line = null;
        try
        {
            snapshot = GetDebugInferenceSnapshot(language);
            if (!snapshot.Ready) throw new InvalidOperationException(snapshot.Readiness);
            if (snapshot.Running) throw new InvalidOperationException("A debug inference or playback operation is already active");
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (debugCancellationGate) debugCancellation = cancellation;
            Volatile.Write(ref debugRunning, 1);
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
                State = DubLineState.Generating,
                PlaybackDeadline = DateTimeOffset.MaxValue,
            };
            audio!.Play(line, configuration.Volume);
            await synthesize(line, cancellation.Token).ConfigureAwait(false);
            Volatile.Write(ref debugStatus, $"{kind}: synthesis passed; playback active");
            line = null; // AudioEngine owns disposal after playback.
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true || token.IsCancellationRequested)
        {
            audio?.Stop();
            line?.Cancel();
            line?.Dispose();
            Volatile.Write(ref debugStatus, $"{kind}: cancelled");
            throw;
        }
        catch (Exception error)
        {
            audio?.Stop();
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
            cancellation?.Dispose();
            Volatile.Write(ref debugRunning, 0);
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        CancelDebugInference();
        await debugGate.WaitAsync().ConfigureAwait(false);
        debugGate.Release();
        cutscenes.Started -= OnCutsceneStarted;
        cutscenes.Ended -= OnCutsceneEnded;
        talk.LineChanged -= OnLineChanged;
        talk.Advanced -= OnTalkClosed;
        talk.Hidden -= OnTalkClosed;
        talk.Finalized -= OnTalkClosed;
        client.TerritoryChanged -= OnTerritoryChanged;
        client.Logout -= OnLogout;
        bootstrap.Ready -= OnRuntimeReady;
        bootstrap.VoiceDesignReady -= OnVoiceDesignReady;
        if (runtimeManager is not null) runtimeManager.SelectionChanged -= OnBackendSelectionChanged;
        nativeVoice.TalkVoiceStarted -= OnNativeVoiceStarted;
        nativeVoice.OfficialVoiceClipObserved -= OnOfficialVoiceClipObserved;
        if (officialReferences is not null) officialReferences.ProfileBuilt -= OnOfficialProfileBuilt;
        await eventGate.WaitAsync().ConfigureAwait(false);
        try { CancelSession(); }
        finally { eventGate.Release(); }
        if (scheduler is not null) await scheduler.DisposeAsync().ConfigureAwait(false);
        if (voiceDesignerInitialization is not null) await voiceDesignerInitialization.ConfigureAwait(false);
        if (domainPool is not null) await domainPool.DisposeAsync().ConfigureAwait(false);
        if (officialReferences is not null) await officialReferences.DisposeAsync().ConfigureAwait(false);
        if (voiceDesigner is not null) await voiceDesigner.DisposeAsync().ConfigureAwait(false);
        audio?.Dispose();
        await lipSync.DisposeAsync().ConfigureAwait(false);
        debugGate.Dispose();
        eventGate.Dispose();
    }
}
