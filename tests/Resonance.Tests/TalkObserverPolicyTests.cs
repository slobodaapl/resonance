using Resonance.Game;

namespace Resonance.Tests;

public sealed class TalkObserverPolicyTests
{
    [Fact]
    public void OnlyAutomaticTimerTicksAreSuppressed()
    {
        var timerTick = AutoAdvanceDiagnosticGate.TimerTickEventType;
        Assert.True(AutoAdvanceDiagnosticGate.ShouldSuppressAutomaticAdvance(timerTick, true));
        Assert.False(AutoAdvanceDiagnosticGate.ShouldSuppressAutomaticAdvance((byte)(timerTick - 1), true));
        Assert.False(AutoAdvanceDiagnosticGate.ShouldSuppressAutomaticAdvance((byte)(timerTick + 1), true));
        Assert.False(AutoAdvanceDiagnosticGate.ShouldSuppressAutomaticAdvance(timerTick, false));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void SyntheticAdvanceRequiresAVisibleAdvanceControl(
        bool manualAdvanceVisible, bool automaticAdvanceVisible, bool expected) =>
        Assert.Equal(expected,
            TalkAdvancePolicy.IsPresentationReady(manualAdvanceVisible, automaticAdvanceVisible));

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(null, true, false)]
    [InlineData(false, null, false)]
    public void GameControlsPacingOnlyForKnownAutomaticOnlyPresentation(
        bool? manualAdvanceVisible, bool? automaticAdvanceVisible, bool expected) =>
        Assert.Equal(expected,
            TalkAdvancePolicy.IsAutomaticOnlyPresentation(
                manualAdvanceVisible, automaticAdvanceVisible));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(null, true, false)]
    public void GameControlledPacingRequiresUnskippableAutomaticOnlyCutscene(
        bool? cutsceneUnskippable, bool automaticOnlyPresentation, bool expected) =>
        Assert.Equal(expected,
            TalkAdvancePolicy.ShouldPreserveGameControlledPacing(
                cutsceneUnskippable, automaticOnlyPresentation));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void WholeUpdateIsFrozenOnlyAfterPresentationCompletes(
        bool suppressionEnabled, bool presentationReady, bool expected) =>
        Assert.Equal(expected,
            TalkAdvancePolicy.ShouldFreezeAutomaticAdvance(suppressionEnabled, presentationReady));

    [Theory]
    [InlineData(7L, 7L, 7L, true)]
    [InlineData(7L, 7L, 0L, false)]
    [InlineData(7L, 8L, 7L, false)]
    [InlineData(7L, null, 7L, false)]
    public void SyntheticPlaybackRequiresExactCurrentPresentationReadySerial(
        long expectedSerial, long? currentSerial, long? presentationReadySerial, bool expected) =>
        Assert.Equal(expected, TalkAdvancePolicy.CanStartSyntheticPlayback(
            expectedSerial, currentSerial, presentationReadySerial));
}
