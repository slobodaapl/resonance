namespace Resonance.Game;

public static class TalkAdvancePolicy
{
    public static bool IsPresentationReady(bool manualAdvanceVisible, bool automaticAdvanceVisible) =>
        manualAdvanceVisible || automaticAdvanceVisible;

    public static bool ShouldFreezeAutomaticAdvance(bool suppressionEnabled, bool presentationReady) =>
        suppressionEnabled && presentationReady;

    public static bool CanStartSyntheticPlayback(
        long expectedSerial, long? currentSerial, long? presentationReadySerial) =>
        currentSerial == expectedSerial && presentationReadySerial == expectedSerial;
}
