using Dalamud.Plugin.Services;

namespace Resonance.Game;

public sealed class ScdExtractor(IDataManager dataManager, IFramework framework, IPluginLog? log = null)
{
    public async Task<float[]> ExtractMono24KhzAsync(string path, uint soundNumber, CancellationToken token)
    {
        // IDataManager is framework-affine.  Copy the immutable resource bytes
        // on that thread, then perform all decoding on the worker thread.
        var data = await CaptureResourceAsync(path, token).ConfigureAwait(false);
        return await Task.Run(() => ScdAudioDecoder.Extract(data, soundNumber, token), token)
            .ConfigureAwait(false);
    }

    private Task<byte[]> CaptureResourceAsync(string path, CancellationToken token)
    {
        if (framework.IsFrameworkUnloading)
            return Task.FromCanceled<byte[]>(new CancellationToken(true));
        if (framework.IsInFrameworkUpdateThread)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var resource = dataManager.GetFile(path)
                    ?? throw new FileNotFoundException("SCD resource is unavailable", path);
                return Task.FromResult(resource.Data.ToArray());
            }
            catch (Exception error) { return Task.FromException<byte[]>(error); }
        }
        var completion = new TaskCompletionSource<byte[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var dispatch = framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    if (framework.IsFrameworkUnloading)
                    {
                        completion.TrySetCanceled();
                        return;
                    }
                    if (token.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(token);
                        return;
                    }
                    var resource = dataManager.GetFile(path)
                        ?? throw new FileNotFoundException("SCD resource is unavailable", path);
                    completion.TrySetResult(resource.Data.ToArray());
                }
                catch (Exception error) { completion.TrySetException(error); }
            });
            _ = ObserveDispatchAsync(dispatch, completion);
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
            _ = ObserveDispatchAsync(Task.CompletedTask, completion);
        }
        return completion.Task.WaitAsync(token);
    }

    private async Task ObserveDispatchAsync(
        Task dispatch, TaskCompletionSource<byte[]> completion)
    {
        Exception? failure = null;
        try { await dispatch.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            completion.TrySetCanceled();
        }
        catch (Exception error)
        {
            failure = error;
            completion.TrySetException(error);
        }
        try { await completion.Task.ConfigureAwait(false); }
        catch (Exception error) { failure ??= error; }
        if (failure is not null && failure is not OperationCanceledException)
            log?.Warning(failure is AggregateException aggregate ? aggregate.GetBaseException() : failure,
                "SCD resource framework dispatch failed");
    }
}
