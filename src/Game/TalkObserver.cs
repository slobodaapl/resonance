using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace Resonance.Game;

public sealed record ActualTalkLine(long Serial, string Speaker, string Text, DateTimeOffset ObservedAt,
    DialogueActor? Actor = null, DialogueRow? Row = null, DialogueVoice? NativeVoice = null)
{
    public string? RowKey => Row?.Key;
}

public sealed partial class TalkObserver : IDisposable
{
    private readonly IAddonLifecycle lifecycle;
    private readonly IGameGui gameGui;
    private readonly NativeTalkIdentity identity;
    private readonly Func<bool> diagnosticsEnabled;
    private readonly Func<bool> suppressAutomaticAdvance;
    private string lastSpeaker = string.Empty;
    private string lastText = string.Empty;
    private long lastPresentation;
    private long nativeVoiceStartedSerial;
    private bool visible;
    private long serial;
    private long updateGeneration;
    private long lineObservedUpdateGeneration;
    private long presentationReadySerial;
    private nint addonAddress;

    public ActualTalkLine? Current { get; private set; }
    public event Action<ActualTalkLine>? LineChanged;
    public event Action<ActualTalkLine>? PresentationReady;
    public event Action<ActualTalkLine>? NativeVoiceStarted;
    public event Action<ActualTalkLine?>? Advanced;
    public event Action<ActualTalkLine?>? Hidden;
    public event Action<ActualTalkLine?>? Finalized;
    public event Action<AutoAdvanceReceiveSnapshot>? AutoAdvanceReceiveObserved;
    public event Action<AutoAdvanceUiSnapshot>? AutoAdvanceUiObserved;

    public TalkObserver(IAddonLifecycle lifecycle, IGameGui gameGui, IGameInteropProvider interop,
        ISigScanner scanner, IPluginLog log, Func<bool> diagnosticsEnabled,
        Func<bool>? suppressAutomaticAdvance = null)
    {
        this.lifecycle = lifecycle;
        this.gameGui = gameGui;
        identity = new NativeTalkIdentity(lifecycle, interop, scanner, log);
        this.diagnosticsEnabled = diagnosticsEnabled;
        this.suppressAutomaticAdvance = suppressAutomaticAdvance ?? (() => false);
        lifecycle.RegisterListener(AddonEvent.PreUpdate, "Talk", OnPreUpdate);
        lifecycle.RegisterListener(AddonEvent.PostDraw, "Talk", OnObserved);
        lifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", OnObserved);
        lifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", OnObserved);
        lifecycle.RegisterListener(AddonEvent.PostRefresh, "Talk", OnObserved);
        lifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "Talk", OnReceiveEvent);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "Talk", OnFinalize);
    }

    private unsafe void OnPreUpdate(AddonEvent _, AddonArgs args)
    {
        Interlocked.Increment(ref updateGeneration);
        var addon = (AddonTalk*)args.Addon.Address;
        if (addon != null && TalkAdvancePolicy.ShouldFreezeAutomaticAdvance(
                suppressAutomaticAdvance(), Current?.Serial == presentationReadySerial))
            args.PreventOriginal();
    }

    private unsafe void OnObserved(AddonEvent _, AddonArgs args)
    {
        var diagnostics = diagnosticsEnabled();
        var addon = (AddonTalk*)args.Addon.Address;
        if (addon == null || !addon->IsVisible)
        {
            if (diagnostics) EmitUiSnapshot(addon);
            ObserveHidden();
            return;
        }

        visible = true;
        addonAddress = args.Addon.Address;
        var speaker = Normalize(addon->AtkTextNode220 == null
            ? string.Empty
            : new ReadOnlySeString(addon->AtkTextNode220->NodeText).ToString());
        if (speaker.Length == 0) speaker = "Narrator";
        var text = Normalize(new ReadOnlySeString(addon->String268).ToString());
        var presentation = identity.Read(args.Addon.Address);
        if (text.Length == 0 || (speaker == lastSpeaker && text == lastText
            && (presentation?.Sequence ?? 0) == lastPresentation))
        {
            if (text.Length != 0) ObserveNativeVoice();
            if (Current is { } current
                && Volatile.Read(ref updateGeneration) > lineObservedUpdateGeneration
                && presentationReadySerial != current.Serial
                && IsPresentationReady(addon))
            {
                presentationReadySerial = current.Serial;
                PresentationReady?.Invoke(current);
            }
            if (diagnostics) EmitUiSnapshot(addon);
            return;
        }

        lastSpeaker = speaker;
        lastText = text;
        lastPresentation = presentation?.Sequence ?? 0;
        Current = new ActualTalkLine(Interlocked.Increment(ref serial), speaker, text,
            DateTimeOffset.UtcNow, presentation?.Actor, presentation?.Row, presentation?.NativeVoice);
        lineObservedUpdateGeneration = Volatile.Read(ref updateGeneration);
        presentationReadySerial = 0;
        if (diagnostics) EmitUiSnapshot(addon);
        LineChanged?.Invoke(Current);
        ObserveNativeVoice();
    }

    private void ObserveNativeVoice()
    {
        if (Current is not { NativeVoice: { } voice } current
            || nativeVoiceStartedSerial == current.Serial || !identity.IsVoicePlaying(voice)) return;
        nativeVoiceStartedSerial = current.Serial;
        NativeVoiceStarted?.Invoke(current);
    }

    private unsafe void OnReceiveEvent(AddonEvent _, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs receive) return;
        var type = (AtkEventType)receive.AtkEventType;
        if (diagnosticsEnabled()) EmitReceiveSnapshot(receive, type);
        if (AutoAdvanceDiagnosticGate.ShouldSuppressAutomaticAdvance(
                (byte)type, suppressAutomaticAdvance()))
        {
            receive.PreventOriginal();
            return;
        }
        var data = (AtkEventData*)receive.AtkEventData;
        if (data == null) return;
        var advances = type == AtkEventType.InputReceived
                       && TalkAdvancePolicy.IsAdvanceInput(data->InputData.InputId)
                       || type == AtkEventType.MouseClick
                       && ((byte)data->MouseData.Modifier & 0b0001_0000) == 0;
        if (advances && Current?.Serial == presentationReadySerial) Advanced?.Invoke(Current);
    }

    private unsafe void EmitReceiveSnapshot(AddonReceiveEventArgs receive, AtkEventType type)
    {
        try
        {
            var eventValue = (AtkEvent*)receive.AtkEvent;
            uint? eventParam = null;
            byte? eventStateType = null;
            byte? eventStateReturnFlags = null;
            byte? eventStateFlags = null;
            if (eventValue != null)
            {
                eventParam = eventValue->Param;
                var state = eventValue->State;
                eventStateType = (byte)state.EventType;
                eventStateReturnFlags = state.ReturnFlags;
                eventStateFlags = (byte)state.StateFlags;
            }

            var data = (AtkEventData*)receive.AtkEventData;
            byte? mouseButtonId = null;
            byte? mouseModifier = null;
            short? mouseX = null;
            short? mouseY = null;
            int? inputId = null;
            byte? inputState = null;
            byte? inputModifier = null;
            if (data != null)
            {
                var mouse = data->MouseData;
                var input = data->InputData;
                mouseButtonId = mouse.ButtonId;
                mouseModifier = (byte)mouse.Modifier;
                mouseX = mouse.PosX;
                mouseY = mouse.PosY;
                inputId = input.InputId;
                inputState = (byte)input.State;
                inputModifier = (byte)input.Modifier;
            }

            var snapshot = new AutoAdvanceReceiveSnapshot(
                Current?.Serial,
                (byte)type,
                receive.EventParam,
                eventParam,
                eventStateType,
                eventStateReturnFlags,
                eventStateFlags,
                mouseButtonId,
                mouseModifier,
                mouseX,
                mouseY,
                inputId,
                inputState,
                inputModifier);
            AutoAdvanceReceiveObserved?.Invoke(snapshot);
        }
        catch { /* Diagnostics must not affect the native event path. */ }
    }

    private unsafe void EmitUiSnapshot(AddonTalk* addon)
    {
        var agent = ReadCutsceneAgent();
        var setting = ReadAddon("TalkAutoMessageSetting");
        var selector = ReadAddon("TalkAutoMessageSelector");
        var selectorCancel = ReadAddon("SelectYesno");
        var snapshot = new AutoAdvanceUiSnapshot(
            Current?.Serial,
            ReadTalkNodeVisibility(addon, 8),
            ReadTalkNodeVisibility(addon, 9),
            agent.Present,
            agent.Active,
            agent.Ready,
            agent.Shown,
            agent.TalkAutoMessageSettingAddonId,
            agent.TalkAutoMessageSelectorAddonId,
            agent.TalkAutoMessageSelectorCancelAddonId,
            agent.PendingTextAutoAdvanceSetting,
            agent.PendingUnvoicedAutoAdvanceSpeed,
            setting.Present,
            setting.Visible,
            selector.Present,
            selector.Visible,
            selectorCancel.Present,
            selectorCancel.Visible);
        try { AutoAdvanceUiObserved?.Invoke(snapshot); }
        catch { /* Diagnostics must not affect the Talk observation path. */ }
    }

    private static unsafe bool? ReadTalkNodeVisibility(AddonTalk* addon, uint id)
    {
        try
        {
            if (addon == null) return null;
            var node = addon->UldManager.SearchNodeById(id);
            return node == null ? null : node->IsVisible();
        }
        catch { return null; }
    }

    private unsafe (bool? Present, bool? Active, bool? Ready, bool? Shown,
        uint? TalkAutoMessageSettingAddonId, uint? TalkAutoMessageSelectorAddonId,
        uint? TalkAutoMessageSelectorCancelAddonId, byte? PendingTextAutoAdvanceSetting,
        byte? PendingUnvoicedAutoAdvanceSpeed) ReadCutsceneAgent()
    {
        try
        {
            var agent = gameGui.GetAgentById((int)AgentId.Cutscene);
            if (agent.IsNull)
                return (false, null, null, null, null, null, null, null, null);

            var cutscene = (AgentCutscene*)agent.Address;
            return (true, agent.IsAgentActive, agent.IsAddonReady, agent.IsAddonShown,
                cutscene == null ? null : cutscene->TalkAutoMessageSettingAddonId,
                cutscene == null ? null : cutscene->TalkAutoMessageSelectorAddonId,
                cutscene == null ? null : cutscene->TalkAutoMessageSelectorCancelAddonId,
                cutscene == null ? null : cutscene->PendingTextAutoAdvanceSetting,
                cutscene == null ? null : cutscene->PendingUnvoicedAutoAdvanceSpeed);
        }
        catch
        {
            return (null, null, null, null, null, null, null, null, null);
        }
    }

    private (bool? Present, bool? Visible) ReadAddon(string name)
    {
        try
        {
            var addon = gameGui.GetAddonByName(name);
            return addon.IsNull ? (false, null) : (true, addon.IsVisible);
        }
        catch { return (null, null); }
    }

    private void OnFinalize(AddonEvent _, AddonArgs __)
    {
        var previous = Current;
        Reset();
        Finalized?.Invoke(previous);
    }

    private void ObserveHidden()
    {
        if (!visible) return;
        var previous = Current;
        Reset();
        Hidden?.Invoke(previous);
    }

    private void Reset()
    {
        visible = false;
        addonAddress = 0;
        presentationReadySerial = 0;
        lineObservedUpdateGeneration = 0;
        Current = null;
        lastSpeaker = string.Empty;
        lastText = string.Empty;
        lastPresentation = 0;
    }

    private static string Normalize(string value) => Whitespace().Replace(value, " ").Trim();

    public unsafe bool TryAdvance(long serial, string speaker, string text)
    {
        var current = Current;
        var addon = (AddonTalk*)addonAddress;
        if (!visible || addon == null || !addon->IsVisible || current is null
            || current.Serial != serial || current.Speaker != speaker || current.Text != text) return false;
        if (!IsPresentationReady(serial)) return false;

        if (identity.TryAdvance((nint)addon) is { } advanced)
        {
            if (advanced) Advanced?.Invoke(current);
            return advanced;
        }

        var eventValue = new AtkEvent
        {
            Listener = (AtkEventListener*)addon,
            Target = &AtkStage.Instance()->AtkEventTarget,
            State = new() { StateFlags = (AtkEventStateFlags)132 },
        };
        AtkEventData eventData = default;
        // One confirm cannot deliver trailing mouse events to the next Talk presentation.
        eventData.InputData.InputId = 1;
        addon->ReceiveEvent(AtkEventType.InputReceived, 0, &eventValue, &eventData);
        return true;
    }

    public unsafe bool IsPresentationReady(long serial) =>
        visible && TalkAdvancePolicy.CanStartSyntheticPlayback(
            serial, Current?.Serial, presentationReadySerial, lastPresentation,
            identity.Read(addonAddress)?.Sequence ?? 0, LiveContentMatches((AddonTalk*)addonAddress));

    private unsafe bool LiveContentMatches(AddonTalk* addon)
    {
        if (addon == null || !addon->IsVisible || Current is not { } current) return false;
        var speaker = Normalize(addon->AtkTextNode220 == null ? String.Empty
            : new ReadOnlySeString(addon->AtkTextNode220->NodeText).ToString());
        if (speaker.Length == 0) speaker = "Narrator";
        return speaker == current.Speaker
            && Normalize(new ReadOnlySeString(addon->String268).ToString()) == current.Text;
    }

    private static unsafe bool IsPresentationReady(AddonTalk* addon)
    {
        var manualAdvance = addon->UldManager.SearchNodeById(8);
        var automaticAdvance = addon->UldManager.SearchNodeById(9);
        return TalkAdvancePolicy.IsPresentationReady(
            manualAdvance != null && manualAdvance->IsVisible(),
            automaticAdvance != null && automaticAdvance->IsVisible());
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public void Dispose()
    {
        identity.Dispose();
        lifecycle.UnregisterListener(OnPreUpdate);
        lifecycle.UnregisterListener(OnObserved);
        lifecycle.UnregisterListener(OnReceiveEvent);
        lifecycle.UnregisterListener(OnFinalize);
    }
}
