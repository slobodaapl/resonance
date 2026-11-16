namespace Resonance.Game;

public static class TalkAdvancePolicy
{
    public static bool IsAdvanceInput(int inputId) => inputId is 1 or 3;

    public static bool CanClosePresentation(long? closedSerial, long? currentSerial) =>
        currentSerial is null || closedSerial == currentSerial;

    public static bool IsPresentationReady(bool manualAdvanceVisible, bool automaticAdvanceVisible) =>
        manualAdvanceVisible || automaticAdvanceVisible;

    public static bool ShouldFreezeAutomaticAdvance(bool suppressionEnabled, bool presentationReady) =>
        suppressionEnabled && presentationReady;

    public static bool CanStartSyntheticPlayback(
        long expectedSerial, long? currentSerial, long? presentationReadySerial,
        long observedPresentation = 0, long nativePresentation = 0, bool liveContentMatches = true) =>
        currentSerial == expectedSerial && presentationReadySerial == expectedSerial
        && observedPresentation == nativePresentation && liveContentMatches;
}
