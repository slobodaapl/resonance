namespace Resonance.Game;

internal sealed record PreDubScene(uint CutsceneId, CutsceneVoiceLine[] Lines);

internal sealed class CutscenePreDubPlanner(
    Func<CutsceneManifestKey, CutsceneVoiceManifest?> load,
    Action<Action> dispatch,
    Action<Exception> failed) : IAsyncDisposable
{
    private readonly object gate = new();
    private Task worker = Task.CompletedTask;
    private CancellationTokenSource? active;
    private string? requestedKey;
    private long revision;
    private bool disposed;

    public Task Request(string key, IReadOnlyList<uint> candidates, CutsceneManifestKey context,
        Action<IReadOnlyList<PreDubScene>> ready,
        Func<CutsceneVoiceLine, CancellationToken, ValueTask<bool>>? canPrepare = null)
    {
        CancellationTokenSource? previous;
        Task task;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (requestedKey == key) return worker;
            previous = active;
            var cancellation = active = new CancellationTokenSource();
            var generation = ++revision;
            requestedKey = key;
            var prior = worker;
            var snapshot = candidates.ToArray();
            task = worker = Task.Run(() => PlanAsync(prior, generation, snapshot, context, cancellation, ready, canPrepare));
        }
        Cancel(previous);
        return task;
    }

    public void Cancel()
    {
        CancellationTokenSource? previous;
        lock (gate)
        {
            ++revision;
            requestedKey = null;
            previous = active;
            active = null;
        }
        Cancel(previous);
    }

    private async Task PlanAsync(Task prior, long generation, uint[] candidates, CutsceneManifestKey context,
        CancellationTokenSource cancellation, Action<IReadOnlyList<PreDubScene>> ready,
        Func<CutsceneVoiceLine, CancellationToken, ValueTask<bool>>? canPrepare)
    {
        var token = cancellation.Token;
        try
        {
            await prior.ConfigureAwait(false);
            var scenes = new List<PreDubScene>();
            foreach (var id in candidates)
            {
                token.ThrowIfCancellationRequested();
                var manifest = load(context with { CutsceneId = id });
                if (manifest is null) continue;
                var lines = new List<CutsceneVoiceLine>();
                foreach (var line in manifest.Lines)
                {
                    token.ThrowIfCancellationRequested();
                    if (!line.IsVoiced && !line.IsPlayerChoice
                        && (canPrepare is null || await canPrepare(line, token).ConfigureAwait(false)))
                        lines.Add(line);
                }
                if (lines.Count == 0) continue;
                scenes.Add(new(id, lines.ToArray()));
                if (scenes.Count == 2) break;
            }
            token.ThrowIfCancellationRequested();
            dispatch(() =>
            {
                lock (gate)
                    if (disposed || revision != generation) return;
                try { ready(scenes); }
                catch (Exception error) { ReportFailure(generation, error); }
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { ReportFailure(generation, error); }
        finally
        {
            lock (gate)
                if (ReferenceEquals(active, cancellation)) active = null;
            cancellation.Dispose();
        }
    }

    private void ReportFailure(long generation, Exception error)
    {
        lock (gate)
        {
            if (disposed || revision != generation) return;
            requestedKey = null;
        }
        failed(error);
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) disposed = true;
        Cancel();
        lock (gate) return new(worker);
    }
}
