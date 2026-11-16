namespace Resonance.Scheduling;

public static class AutoAdvancePolicy
{
    public static bool CanAdvanceCompletedLine(DubLine line, long currentEpoch, long currentTalkSerial) =>
        line.SessionEpoch == currentEpoch && line.ActualTalkSerial == currentTalkSerial
        && line.ActualStatus == ActualStatus.Actual && line.State == DubLineState.Completed
        && line.NativeVoiceStatus != NativeVoiceStatus.NativeVoiced;
}
