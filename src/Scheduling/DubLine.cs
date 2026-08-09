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
    public DubLineState State { get; set; }
    public string? VoiceProfileId { get; set; }
    public string? VoiceProfileHash { get; set; }
    public long? SpeakerId { get; set; }
    public bool DirectSynthesisCompleted { get; set; }
    public bool CanStartStreaming { get; set; }
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
            DirectSynthesisCompleted = false;
            CanStartStreaming = false;
        }
    }

    public void Cancel(DubLineState terminal = DubLineState.Cancelled)
    {
        lock (lifecycleGate)
        {
            if (disposed || IsTerminal) return;
            State = terminal;
            Cancellation.Cancel();
            Audio.Complete();
        }
    }

    public void ReplaceAudio(StreamingAudioBuffer replacement)
    {
        var previous = Audio;
        Audio = replacement;
        previous.Dispose();
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
                Audio.Complete();
            }
            Cancellation.Dispose();
            Audio.Dispose();
        }
    }
}
