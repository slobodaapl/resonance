namespace Resonance.Game;

public sealed record DialogueActor(uint BaseId, ulong GameObjectId, nint Address, byte ObjectKind)
{
    public uint? NpcBaseId => ObjectKind == 3 ? BaseId : null;
    public string StableKey => ObjectKind == 3 ? $"npc:{BaseId}" : $"bnpc:{BaseId}";
    public bool Matches(uint baseId, ulong gameObjectId, nint address, byte objectKind) =>
        BaseId == baseId && GameObjectId == gameObjectId && Address == address && ObjectKind == objectKind;
}
public sealed record DialogueRow(uint CutsceneId, int Offset, string Key);
public sealed record DialogueVoice(nint Handle, string ResourcePath);
public sealed record DialoguePresentation(long Sequence, DialogueActor? Actor, DialogueRow? Row = null,
    DialogueVoice? NativeVoice = null)
{
    public string? RowKey => Row?.Key;
}

public sealed class DialogueIdentity
{
    private readonly Dictionary<(nint Owner, nint Entry),
        (DialogueActor? Actor, DialogueRow? Row, DialogueVoice? NativeVoice)> queued = [];
    private readonly Dictionary<nint, (string Text, DialoguePresentation Presentation)> presentations = [];
    private long sequence;
    private DialogueActor? presenting;
    private DialogueRow? presentingRow;
    private DialogueVoice? presentingVoice;
    private bool dispatching;

    public void Dispatch(DialogueActor? actor, Action nativeDispatch, DialogueRow? row = null,
        DialogueVoice? nativeVoice = null)
    {
        var previous = presenting;
        var previousDispatching = dispatching;
        var previousRow = presentingRow;
        var previousVoice = presentingVoice;
        try
        {
            presenting = actor;
            presentingRow = row;
            presentingVoice = nativeVoice;
            dispatching = true;
            nativeDispatch();
        }
        finally
        {
            presenting = previous;
            presentingRow = previousRow;
            presentingVoice = previousVoice;
            dispatching = previousDispatching;
        }
    }

    public void Queue(nint owner, nint entry, DialogueActor? actor, DialogueRow? row = null,
        DialogueVoice? nativeVoice = null) => queued[(owner, entry)] = (actor, row, nativeVoice);

    public (DialogueActor? Actor, DialogueRow? Row, DialogueVoice? NativeVoice)? TakeQueued(nint owner, nint entry)
    {
        return queued.Remove((owner, entry), out var source) ? source : null;
    }

    public void RetainQueued(nint owner, IReadOnlySet<nint> entries)
    {
        foreach (var key in queued.Keys.Where(key => key.Owner == owner && !entries.Contains(key.Entry)).ToArray())
            queued.Remove(key);
    }

    public void Present(nint addon, string text = "")
    {
        if (!dispatching && presentations.TryGetValue(addon, out var previous) && previous.Text == text) return;
        presentations[addon] = (text, new(++sequence, presenting, presentingRow, presentingVoice));
    }

    public DialoguePresentation? Read(nint addon) =>
        presentations.TryGetValue(addon, out var value) ? value.Presentation : null;
    public void Finalize(nint addon) => presentations.Remove(addon);
    public void Clear()
    {
        queued.Clear();
        presentations.Clear();
        presenting = null;
        presentingRow = null;
        presentingVoice = null;
        dispatching = false;
    }
}
