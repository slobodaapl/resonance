using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace Resonance.Game;

public sealed record ActualTalkLine(long Serial, string Speaker, string Text, DateTimeOffset ObservedAt);

public sealed partial class TalkObserver : IDisposable
{
    private readonly IAddonLifecycle lifecycle;
    private string lastSpeaker = string.Empty;
    private string lastText = string.Empty;
    private bool visible;
    private long serial;

    public ActualTalkLine? Current { get; private set; }
    public event Action<ActualTalkLine>? LineChanged;
    public event Action<ActualTalkLine?>? Advanced;
    public event Action<ActualTalkLine?>? Hidden;
    public event Action<ActualTalkLine?>? Finalized;

    public TalkObserver(IAddonLifecycle lifecycle)
    {
        this.lifecycle = lifecycle;
        lifecycle.RegisterListener(AddonEvent.PostDraw, "Talk", OnObserved);
        lifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", OnObserved);
        lifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "Talk", OnReceiveEvent);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "Talk", OnFinalize);
    }

    private unsafe void OnObserved(AddonEvent _, AddonArgs args)
    {
        var addon = (AddonTalk*)args.Addon.Address;
        if (addon == null || !addon->IsVisible)
        {
            ObserveHidden();
            return;
        }

        visible = true;
        var speaker = Normalize(addon->AtkTextNode220 == null
            ? string.Empty
            : new ReadOnlySeString(addon->AtkTextNode220->NodeText).ToString());
        if (speaker.Length == 0) speaker = "Narrator";
        var text = Normalize(new ReadOnlySeString(addon->String268).ToString());
        if (text.Length == 0 || (speaker == lastSpeaker && text == lastText)) return;

        lastSpeaker = speaker;
        lastText = text;
        Current = new ActualTalkLine(Interlocked.Increment(ref serial), speaker, text, DateTimeOffset.UtcNow);
        LineChanged?.Invoke(Current);
    }

    private unsafe void OnReceiveEvent(AddonEvent _, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs receive) return;
        var data = (AtkEventData*)receive.AtkEventData;
        if (data == null) return;
        var type = (AtkEventType)receive.AtkEventType;
        var advances = type == AtkEventType.InputReceived
                       || type == AtkEventType.MouseClick
                       && ((byte)data->MouseData.Modifier & 0b0001_0000) == 0;
        if (advances) Advanced?.Invoke(Current);
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
        Current = null;
        lastSpeaker = string.Empty;
        lastText = string.Empty;
    }

    private static string Normalize(string value) => Whitespace().Replace(value, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public void Dispose()
    {
        lifecycle.UnregisterListener(OnObserved);
        lifecycle.UnregisterListener(OnReceiveEvent);
        lifecycle.UnregisterListener(OnFinalize);
    }
}
