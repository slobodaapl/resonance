using Resonance.Game;

namespace Resonance.Tests;

public sealed class TalkObserverPolicyTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(0, false)]
    public void OnlyNativeConfirmInputsAdvanceDialogue(int inputId, bool expected) =>
        Assert.Equal(expected, TalkAdvancePolicy.IsAdvanceInput(inputId));

    [Theory]
    [InlineData(7L, 7L, true)]
    [InlineData(7L, 8L, false)]
    [InlineData(7L, null, true)]
    [InlineData(null, 8L, false)]
    public void QueuedCloseCannotCancelANewerPresentation(long? closedSerial, long? currentSerial, bool expected) =>
        Assert.Equal(expected, TalkAdvancePolicy.CanClosePresentation(closedSerial, currentSerial));

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

    [Fact]
    public void NativePresentationChangeRejectsAdvanceBeforeCachedSerialCatchesUp()
    {
        Assert.False(TalkAdvancePolicy.CanStartSyntheticPlayback(
            7, 7, 7, observedPresentation: 10, nativePresentation: 11));
    }

    [Fact]
    public void ChangedLiveTextRejectsAdvanceEvenWhenCachedSerialStillMatches()
    {
        Assert.False(TalkAdvancePolicy.CanStartSyntheticPlayback(
            7, 7, 7, observedPresentation: 10, nativePresentation: 10, liveContentMatches: false));
    }

    [Fact]
    public void UnchangedLivePresentationAllowsAdvanceAfterReadiness()
    {
        Assert.True(TalkAdvancePolicy.CanStartSyntheticPlayback(
            7, 7, 7, observedPresentation: 10, nativePresentation: 10, liveContentMatches: true));
    }
}
