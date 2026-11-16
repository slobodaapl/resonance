using Resonance.Scheduling;

namespace Resonance.Tests;

public sealed class AutoAdvancePolicyTests
{
    [Theory]
    [InlineData(DubLineState.Predicted, false)]
    [InlineData(DubLineState.Generating, false)]
    [InlineData(DubLineState.Buffered, false)]
    [InlineData(DubLineState.Active, false)]
    [InlineData(DubLineState.Completed, true)]
    [InlineData(DubLineState.Cancelled, false)]
    [InlineData(DubLineState.Failed, false)]
    [InlineData(DubLineState.NativeVoiced, false)]
    public void AdvancementRequiresCompletedSpeech(DubLineState state, bool expected)
    {
        using var line = Actual();
        Assert.True(line.TryTransition(state, DubLineState.Predicted));
        Assert.Equal(expected, AutoAdvancePolicy.CanAdvanceCompletedLine(line, 4, 7));
    }

    [Fact]
    public void CompletedSpeechCannotAdvanceAnotherSessionOrPresentation()
    {
        using var line = Actual();
        Assert.True(line.TryTransition(DubLineState.Completed, DubLineState.Predicted));
        Assert.False(AutoAdvancePolicy.CanAdvanceCompletedLine(line, 5, 7));
        Assert.False(AutoAdvancePolicy.CanAdvanceCompletedLine(line, 4, 8));
        line.ActualStatus = ActualStatus.Predicted;
        Assert.False(AutoAdvancePolicy.CanAdvanceCompletedLine(line, 4, 7));
        line.ActualStatus = ActualStatus.Actual;
        line.NativeVoiceStatus = NativeVoiceStatus.NativeVoiced;
        Assert.False(AutoAdvancePolicy.CanAdvanceCompletedLine(line, 4, 7));
    }

    [Fact]
    public void UnfinishedSpeculativeBranchesDoNotHoldCompletedDialogue()
    {
        using var session = new CutsceneSession(4, 2);
        var predictions = session.ReconcilePredictions([
            new("left", "npc:1", "First", "One possible branch", "english"),
            new("right", "npc:2", "Second", "Another possible branch", "english"),
        ]);
        Assert.True(predictions[0].TryTransition(DubLineState.Generating, DubLineState.Predicted));
        var actual = session.AddActual("npc:3", "Current", "Finished speech");
        actual.ActualTalkSerial = 7;
        Assert.True(actual.TryTransition(DubLineState.Completed, DubLineState.Queued));

        Assert.True(AutoAdvancePolicy.CanAdvanceCompletedLine(actual, session.Epoch, 7));
        Assert.Equal(DubLineState.Generating, predictions[0].State);
        Assert.Equal(DubLineState.Predicted, predictions[1].State);
    }

    private static DubLine Actual() => new()
    {
        SessionEpoch = 4,
        Sequence = 1,
        SpeakerKey = "npc:3",
        SpeakerName = "Current",
        Text = "Finished speech",
        ActualTalkSerial = 7,
        ActualStatus = ActualStatus.Actual,
        NativeVoiceStatus = NativeVoiceStatus.NotVoiced,
    };
}
