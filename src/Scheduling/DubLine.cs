using Resonance.Audio;
using Resonance.Tts;

namespace Resonance.Scheduling;

public enum ActualStatus { Predicted, Actual }
public enum NativeVoiceStatus { Unknown, NotVoiced, NativeVoiced }
public enum DubLineState
{
    Predicted,
    VoiceResolving,
    Queued,
    Generating,
    Buffered,
    Active,
    Completed,
    NativeVoiced,
    Invalidated,
    Cancelled,
    Failed,
}

public sealed record ResolvedLineSpeaker(
    string SpeakerKey,
    string SpeakerName,
    long SpeakerId,
    SpeakerCastingEvidence Evidence,
    CastingResolution Casting,
    string VoiceSex,
    string VoiceArchetype,
    nint ActorAddress,
    string? CastingSlotId = null,
    string? Language = null);

public sealed class DubLine : IDisposable
{
    public required long SessionEpoch { get; init; }
    public required long Sequence { get; init; }
    public string? SourceQuest { get; init; }
    public required string SpeakerKey { get; set; }
    public required string SpeakerName { get; set; }
    public required string Text { get; set; }
    public long? ActualTalkSerial { get; set; }
    public ActualStatus ActualStatus { get; set; }
    public NativeVoiceStatus NativeVoiceStatus { get; set; }
    public DubLineState State { get; private set; }
    public string? VoiceProfileId { get; set; }
    public string? VoiceProfileHash { get; set; }
    public string? PredictionKey { get; set; }
    public string? OfficialVoiceGroupId { get; set; }
    public string? NextPredictionKey { get; set; }
    public IReadOnlyList<string> NextPredictionKeys { get; set; } = [];
    public long? SpeakerId { get; set; }
    public bool TransientSpeaker { get; set; }
    public bool DirectSynthesisCompleted { get; set; }
    public bool ApplyBaseCloneCorrection { get; set; }
    public bool CanStartStreaming { get; set; }
    public bool PlaybackAssetReady { get; set; }
    public string VoiceArchetype { get; set; } = "neutral_adult";
    public string VoiceSex { get; set; } = "masculine";
    public string? Language { get; set; }
    public nint ActorAddress { get; set; }
    public SpeakerCastingEvidence? CastingEvidence { get; set; }
    public CastingResolution? Casting { get; set; }
    public string? CastingSlotId { get; set; }
    public double PredictedAudioDurationSeconds { get; set; }
    public double EstimatedGenerationSeconds { get; set; }
    public DateTimeOffset PlaybackDeadline { get; set; }
    public StreamingAudioBuffer Audio { get; private set; } = new();
    public CancellationTokenSource Cancellation { get; } = new();
    public CancellationToken Token { get; }
    private readonly object lifecycleGate = new();
    private bool disposed;

    public DubLine() => Token = Cancellation.Token;

    public double SlackSeconds(DateTimeOffset now) =>
        (PlaybackDeadline - now).TotalSeconds - EstimatedGenerationSeconds;

    public bool IsTerminal => State is DubLineState.Completed or DubLineState.NativeVoiced
        or DubLineState.Invalidated or DubLineState.Cancelled or DubLineState.Failed;

    public void ApplyResolvedSpeaker(ResolvedLineSpeaker speaker, bool preservePreparedAudio = false)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        SpeakerKey = speaker.SpeakerKey;
        SpeakerName = speaker.SpeakerName;
        SpeakerId = speaker.SpeakerId;
        CastingEvidence = speaker.Evidence;
        Casting = speaker.Casting;
        CastingSlotId = speaker.CastingSlotId;
        VoiceSex = speaker.VoiceSex;
        VoiceArchetype = speaker.VoiceArchetype;
        if (!String.IsNullOrWhiteSpace(speaker.Language)) Language = speaker.Language;
        ActorAddress = speaker.ActorAddress;
        if (!preservePreparedAudio)
        {
            ReplaceAudio(new StreamingAudioBuffer());
            VoiceProfileId = null;
            VoiceProfileHash = null;
            ApplyBaseCloneCorrection = false;
            DirectSynthesisCompleted = false;
            CanStartStreaming = false;
            PlaybackAssetReady = false;
        }
    }

    public void Cancel(DubLineState terminal = DubLineState.Cancelled)
    {
        lock (lifecycleGate)
        {
            if (disposed || IsTerminal) return;
            State = terminal;
            Cancellation.Cancel();
            Audio.Invalidate();
        }
    }

    public void ReplaceAudio(StreamingAudioBuffer replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (lifecycleGate)
        {
            if (disposed || IsTerminal)
            {
                replacement.Invalidate();
                return;
            }

            var previous = Audio;
            Audio = replacement;
            previous.Dispose();
        }
    }

    public bool TryTransition(DubLineState nextState, params DubLineState[] expectedStates)
    {
        lock (lifecycleGate)
        {
            if (disposed || !IsExpectedState(expectedStates)) return false;
            State = nextState;
            return true;
        }
    }

    public bool TryMarkNativeVoiced(params DubLineState[] expectedStates)
    {
        lock (lifecycleGate)
        {
            if (disposed || !IsExpectedState(expectedStates)) return false;
            NativeVoiceStatus = NativeVoiceStatus.NativeVoiced;
            State = DubLineState.NativeVoiced;
            Cancellation.Cancel();
            Audio.Invalidate();
            return true;
        }
    }

    public bool TryMarkNotVoiced()
    {
        lock (lifecycleGate)
        {
            if (disposed || IsTerminal) return false;
            NativeVoiceStatus = NativeVoiceStatus.NotVoiced;
            return true;
        }
    }

    public bool TryReplaceAudioAndTransition(
        IReadOnlyCollection<DubLineState> expectedStates,
        StreamingAudioBuffer replacement,
        DubLineState nextState) =>
        TryReplaceAudioAndTransition(replacement, nextState, expectedStates.ToArray());

    public bool TryReplaceAudioAndTransition(
        StreamingAudioBuffer replacement,
        DubLineState nextState,
        params DubLineState[] expectedStates)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (lifecycleGate)
        {
            if (disposed || !IsExpectedState(expectedStates))
            {
                replacement.Invalidate();
                return false;
            }

            var previous = Audio;
            Audio = replacement;
            State = nextState;
            previous.Dispose();
            return true;
        }
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed) return;
            disposed = true;
            if (!IsTerminal)
            {
                State = DubLineState.Cancelled;
                Cancellation.Cancel();
                Audio.Invalidate();
            }
            Cancellation.Dispose();
            Audio.Dispose();
        }
    }

    private bool IsExpectedState(IReadOnlyCollection<DubLineState> expectedStates)
    {
        if (expectedStates.Count == 0 || IsTerminal) return false;
        foreach (var expectedState in expectedStates)
            if (State == expectedState) return true;
        return false;
    }
}
