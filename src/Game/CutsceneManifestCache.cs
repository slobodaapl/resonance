namespace Resonance.Game;

internal readonly record struct CutsceneManifestKey(uint CutsceneId, int TextLanguage, int VoiceLanguage);

internal sealed class CutsceneManifestCache(Func<CutsceneManifestKey, CutsceneVoiceManifest?> load)
{
    private const int Capacity = 64;
    private sealed record Entry(CutsceneManifestKey Key, Lazy<CutsceneVoiceManifest?> Manifest);
    private readonly object gate = new();
    private readonly Dictionary<CutsceneManifestKey, LinkedListNode<Entry>> entries = [];
    private readonly LinkedList<Entry> recent = new();

    public CutsceneVoiceManifest? Get(CutsceneManifestKey key)
    {
        LinkedListNode<Entry> node;
        lock (gate)
        {
            if (entries.TryGetValue(key, out node!)) recent.Remove(node);
            else
            {
                node = new(new(key, new(() => load(key))));
                entries.Add(key, node);
            }
            recent.AddLast(node);
            if (entries.Count > Capacity)
            {
                entries.Remove(recent.First!.Value.Key);
                recent.RemoveFirst();
            }
        }

        try
        {
            var manifest = node.Value.Manifest.Value;
            if (manifest is null) Remove(key, node);
            return manifest;
        }
        catch
        {
            Remove(key, node);
            throw;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            recent.Clear();
        }
    }

    private void Remove(CutsceneManifestKey key, LinkedListNode<Entry> expected)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var current) || current != expected) return;
            entries.Remove(key);
            recent.Remove(current);
        }
    }
}
