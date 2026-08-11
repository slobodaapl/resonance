using System.Runtime.ExceptionServices;

namespace Resonance.Plugin;

/// <summary>
/// Prevents a replacement plugin instance from loading native state while a
/// previous instance is still being torn down. A successful generation is
/// cleared; a failed generation remains a process-level restart barrier.
/// </summary>
internal static class ProcessTeardownBarrier
{
    private static readonly object Gate = new();
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);
    private static Task? current;

    public static Task Publish(Task teardown)
    {
        ArgumentNullException.ThrowIfNull(teardown);
        lock (Gate)
        {
            var previous = current;
            current = previous is { Status: not TaskStatus.RanToCompletion }
                ? ChainAsync(previous, teardown)
                : teardown;
            var published = current;
            _ = published.ContinueWith(
                static (completed, state) => ClearCompleted(state!, completed),
                (object)published,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return published;
        }
    }

    public static void Block(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (Gate)
        {
            if (current is { IsFaulted: true } or { IsCanceled: true }) return;
            current = Task.FromException(
                new InvalidOperationException(
                    "Resonance native teardown failed; restart the game before loading the plugin again.",
                    failure));
        }
    }

    public static void ThrowIfBlocked()
    {
        Task? barrier;
        lock (Gate) barrier = current;
        if (barrier is null || barrier.Status == TaskStatus.RanToCompletion) return;
        if (!barrier.IsCompleted)
            throw new InvalidOperationException(
                "A previous Resonance instance is still tearing down; restart the game if plugin loading does not resume.");
        try { barrier.GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                "Resonance native teardown failed; restart the game before loading the plugin again.", error);
        }
    }

    public static async Task WaitAsync(CancellationToken token)
    {
        Task? barrier;
        lock (Gate)
        {
            barrier = current;
            if (barrier?.Status == TaskStatus.RanToCompletion)
            {
                current = null;
                barrier = null;
            }
        }
        if (barrier is null) return;
        try
        {
            await barrier.WaitAsync(WaitTimeout, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                "Resonance native teardown did not complete safely; restart the game before retrying the plugin.",
                error);
        }
        lock (Gate)
        {
            if (ReferenceEquals(current, barrier) && barrier.Status == TaskStatus.RanToCompletion)
                current = null;
        }
    }

    private static async Task ChainAsync(Task previous, Task next)
    {
        Exception? failure = null;
        try { await previous.ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        try { await next.ConfigureAwait(false); }
        catch (Exception error)
        {
            failure = failure is null ? error : new AggregateException(failure, error);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void ClearCompleted(object state, Task completed)
    {
        lock (Gate)
        {
            if (ReferenceEquals(current, state) && completed.Status == TaskStatus.RanToCompletion)
                current = null;
        }
        _ = completed.Exception;
    }
}
