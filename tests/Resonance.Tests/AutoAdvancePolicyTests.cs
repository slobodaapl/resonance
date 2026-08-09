using Resonance.Scheduling;

namespace Resonance.Tests;

public sealed class AutoAdvancePolicyTests
{
    [Fact]
    public void ImmediateCompletedPredictionAllowsAdvance()
    {
        using var next = Prediction(2, DubLineState.Buffered, completed: true);

        Assert.True(AutoAdvancePolicy.IsImmediateNextPredictionPlayable([next], 1));
    }

    [Fact]
    public void ReadyLaterPredictionCannotSkipUnreadyImmediatePrediction()
    {
        using var immediate = Prediction(2, DubLineState.Generating, completed: false);
        using var later = Prediction(3, DubLineState.Buffered, completed: true);

        Assert.False(AutoAdvancePolicy.IsImmediateNextPredictionPlayable([later, immediate], 1));
    }

    [Fact]
    public void CompletedAudioWithoutBufferedStateCannotAdvance()
    {
        using var next = Prediction(2, DubLineState.Failed, completed: true);

        Assert.False(AutoAdvancePolicy.IsImmediateNextPredictionPlayable([next], 1));
    }

    [Fact]
    public void StreamableImmediatePredictionAllowsAdvanceBeforeCompletion()
    {
        using var next = Prediction(2, DubLineState.Generating, completed: false);
        next.CanStartStreaming = true;

        Assert.True(AutoAdvancePolicy.IsImmediateNextPredictionPlayable([next], 1));
    }

    private static DubLine Prediction(long sequence, DubLineState state, bool completed)
    {
        var line = new DubLine
        {
            SessionEpoch = 1,
            Sequence = sequence,
            SpeakerKey = "npc:1",
            SpeakerName = "NPC",
            Text = "Line",
            ActualStatus = ActualStatus.Predicted,
            NativeVoiceStatus = NativeVoiceStatus.Unknown,
            State = state,
            PlaybackDeadline = DateTimeOffset.UtcNow,
        };
        line.Audio.TryWrite([1f]);
        if (completed) line.Audio.Complete();
        return line;
    }
}
