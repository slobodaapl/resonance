using Resonance.Game;

namespace Resonance.Tests;

public sealed class AutoAdvanceDiagnosticsTests
{
    [Fact]
    public void DuplicateReceiveSignatureIsSuppressed()
    {
        var gate = new AutoAdvanceDiagnosticGate();
        var snapshot = Receive(eventParam: 7);

        Assert.Equal(AutoAdvanceReceiveDecision.Observed, gate.ObserveReceive(snapshot));
        Assert.Equal(AutoAdvanceReceiveDecision.Suppressed, gate.ObserveReceive(snapshot));
    }

    [Fact]
    public void DistinctReceiveSignatureIsObserved()
    {
        var gate = new AutoAdvanceDiagnosticGate();

        Assert.Equal(AutoAdvanceReceiveDecision.Observed, gate.ObserveReceive(Receive(eventParam: 7)));
        Assert.Equal(AutoAdvanceReceiveDecision.Observed, gate.ObserveReceive(Receive(eventParam: 8)));
    }

    [Fact]
    public void OnlyFirstTimerTickPerTalkIsObserved()
    {
        var gate = new AutoAdvanceDiagnosticGate();

        Assert.Equal(AutoAdvanceReceiveDecision.Observed,
            gate.ObserveReceive(Receive(type: AutoAdvanceDiagnosticGate.TimerTickEventType, eventParam: 1)));
        Assert.Equal(AutoAdvanceReceiveDecision.Suppressed,
            gate.ObserveReceive(Receive(type: AutoAdvanceDiagnosticGate.TimerTickEventType, eventParam: 2)));
    }

    [Fact]
    public void ReceiveSignaturesAreCappedWithOneTruncationNotice()
    {
        var gate = new AutoAdvanceDiagnosticGate();

        for (var index = 0; index < AutoAdvanceDiagnosticGate.MaxReceiveSignatures; index++)
            Assert.Equal(AutoAdvanceReceiveDecision.Observed, gate.ObserveReceive(Receive(eventParam: index)));

        Assert.Equal(AutoAdvanceReceiveDecision.Truncated, gate.ObserveReceive(Receive(eventParam: 100)));
        Assert.Equal(AutoAdvanceReceiveDecision.Suppressed, gate.ObserveReceive(Receive(eventParam: 101)));
        Assert.Equal(AutoAdvanceReceiveDecision.Suppressed, gate.ObserveReceive(Receive(eventParam: 100)));
    }

    [Fact]
    public void TalkSerialChangeStartsNewReceiveWindow()
    {
        var gate = new AutoAdvanceDiagnosticGate();

        Assert.Equal(AutoAdvanceReceiveDecision.Observed, gate.ObserveReceive(Receive(serial: 1, eventParam: 7)));
        Assert.Equal(AutoAdvanceReceiveDecision.Observed, gate.ObserveReceive(Receive(serial: 2, eventParam: 7)));
    }

    [Fact]
    public void IdenticalUiSnapshotIsSuppressedAndChangedSnapshotIsObserved()
    {
        var gate = new AutoAdvanceDiagnosticGate();
        var snapshot = Ui();

        Assert.True(gate.ObserveUi(snapshot));
        Assert.False(gate.ObserveUi(snapshot));
        Assert.True(gate.ObserveUi(snapshot with { TalkNode8Visible = true }));
    }

    [Fact]
    public void ResetClearsReceiveAndUiWindows()
    {
        var gate = new AutoAdvanceDiagnosticGate();
        var receive = Receive(eventParam: 7);
        var ui = Ui();

        Assert.Equal(AutoAdvanceReceiveDecision.Observed, gate.ObserveReceive(receive));
        Assert.True(gate.ObserveUi(ui));
        gate.Reset();
        Assert.Equal(AutoAdvanceReceiveDecision.Observed, gate.ObserveReceive(receive));
        Assert.True(gate.ObserveUi(ui));
    }

    private static AutoAdvanceReceiveSnapshot Receive(
        long? serial = 1,
        byte type = 1,
        int eventParam = 0) => new(
        serial,
        type,
        eventParam,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);

    private static AutoAdvanceUiSnapshot Ui(long? serial = 1) => new(
        serial,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);
}
