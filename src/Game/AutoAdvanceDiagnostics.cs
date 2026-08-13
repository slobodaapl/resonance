using System.Collections.Generic;

namespace Resonance.Game;

public readonly record struct AutoAdvanceReceiveSnapshot(
    long? TalkSerial,
    byte AtkEventType,
    int EventParam,
    uint? AtkEventParam,
    byte? EventStateType,
    byte? EventStateReturnFlags,
    byte? EventStateFlags,
    byte? MouseButtonId,
    byte? MouseModifier,
    short? MouseX,
    short? MouseY,
    int? InputId,
    byte? InputState,
    byte? InputModifier);

public readonly record struct AutoAdvanceUiSnapshot(
    long? TalkSerial,
    bool? TalkNode8Visible,
    bool? TalkNode9Visible,
    bool? AgentPresent,
    bool? AgentActive,
    bool? AgentReady,
    bool? AgentShown,
    uint? TalkAutoMessageSettingAddonId,
    uint? TalkAutoMessageSelectorAddonId,
    uint? TalkAutoMessageSelectorCancelAddonId,
    byte? PendingTextAutoAdvanceSetting,
    byte? PendingUnvoicedAutoAdvanceSpeed,
    bool? TalkAutoMessageSettingPresent,
    bool? TalkAutoMessageSettingVisible,
    bool? TalkAutoMessageSelectorPresent,
    bool? TalkAutoMessageSelectorVisible,
    bool? TalkAutoMessageSelectorCancelPresent,
    bool? TalkAutoMessageSelectorCancelVisible);

public enum AutoAdvanceReceiveDecision
{
    Suppressed,
    Observed,
    Truncated,
}

public sealed class AutoAdvanceDiagnosticGate
{
    public const byte TimerTickEventType = 64;
    public const int MaxReceiveSignatures = 32;

    public static bool ShouldSuppressAutomaticAdvance(byte eventType, bool suppressionEnabled) =>
        suppressionEnabled && eventType == TimerTickEventType;

    private readonly HashSet<ReceiveSignature> receiveSignatures = [];
    private long? talkSerial;
    private bool timerTickObserved;
    private bool truncationReported;
    private AutoAdvanceUiSnapshot? lastUiSnapshot;

    public AutoAdvanceReceiveDecision ObserveReceive(AutoAdvanceReceiveSnapshot snapshot)
    {
        EnsureTalk(snapshot.TalkSerial);
        if (snapshot.AtkEventType == TimerTickEventType)
        {
            if (timerTickObserved) return AutoAdvanceReceiveDecision.Suppressed;
            timerTickObserved = true;
            return AutoAdvanceReceiveDecision.Observed;
        }

        var signature = new ReceiveSignature(snapshot);
        if (receiveSignatures.Contains(signature)) return AutoAdvanceReceiveDecision.Suppressed;
        if (receiveSignatures.Count >= MaxReceiveSignatures)
        {
            if (truncationReported) return AutoAdvanceReceiveDecision.Suppressed;
            truncationReported = true;
            return AutoAdvanceReceiveDecision.Truncated;
        }

        receiveSignatures.Add(signature);
        return AutoAdvanceReceiveDecision.Observed;
    }

    public bool ObserveUi(AutoAdvanceUiSnapshot snapshot)
    {
        EnsureTalk(snapshot.TalkSerial);
        if (lastUiSnapshot is { } previous && previous == snapshot) return false;
        lastUiSnapshot = snapshot;
        return true;
    }

    public void Reset()
    {
        talkSerial = null;
        timerTickObserved = false;
        truncationReported = false;
        receiveSignatures.Clear();
        lastUiSnapshot = null;
    }

    private void EnsureTalk(long? serial)
    {
        if (talkSerial == serial) return;
        talkSerial = serial;
        timerTickObserved = false;
        truncationReported = false;
        receiveSignatures.Clear();
        lastUiSnapshot = null;
    }

    private readonly record struct ReceiveSignature(
        byte AtkEventType,
        int EventParam,
        uint? AtkEventParam,
        byte? EventStateType,
        byte? EventStateReturnFlags,
        byte? EventStateFlags,
        byte? MouseButtonId,
        byte? MouseModifier,
        short? MouseX,
        short? MouseY,
        int? InputId,
        byte? InputState,
        byte? InputModifier)
    {
        public ReceiveSignature(AutoAdvanceReceiveSnapshot snapshot)
            : this(snapshot.AtkEventType, snapshot.EventParam, snapshot.AtkEventParam,
                snapshot.EventStateType, snapshot.EventStateReturnFlags, snapshot.EventStateFlags,
                snapshot.MouseButtonId, snapshot.MouseModifier, snapshot.MouseX, snapshot.MouseY,
                snapshot.InputId, snapshot.InputState, snapshot.InputModifier)
        {
        }
    }
}
