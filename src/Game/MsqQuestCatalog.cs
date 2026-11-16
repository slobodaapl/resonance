namespace Resonance.Game;

internal sealed class MsqQuestCatalog(Func<int, IReadOnlyList<MsqQuestNode>> load) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<int, Task<IReadOnlyList<MsqQuestNode>>> loading = [];
    private bool disposed;

    public IReadOnlyList<MsqQuestNode>? TryGet(int language)
    {
        lock (gate)
        {
            if (disposed) return null;
            if (!loading.TryGetValue(language, out var task))
                loading.Add(language, task = Task.Run(() => load(language)));
            if (!task.IsCompleted) return null;
            try { return task.GetAwaiter().GetResult(); }
            catch { loading.Remove(language); throw; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (gate)
        {
            disposed = true;
            pending = loading.Values.ToArray();
        }
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch { }
    }
}
