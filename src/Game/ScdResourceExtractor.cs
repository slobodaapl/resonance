using Dalamud.Plugin.Services;

namespace Resonance.Game;

public sealed class ScdExtractor(IDataManager dataManager, IFramework framework, IPluginLog? log = null)
{
    public async Task<float[]> ExtractMono24KhzAsync(string path, uint soundNumber, CancellationToken token)
    {
        // IDataManager is framework-affine.  Copy the immutable resource bytes
        // on that thread, then perform all decoding on the worker thread.
        var data = await CaptureResourceBytesAsync(path, token).ConfigureAwait(false);
        return await Task.Run(() => ScdAudioDecoder.Extract(data, soundNumber, token), token)
            .ConfigureAwait(false);
    }

    public async Task<uint?> ResolveSoleAudioEntryAsync(string path, CancellationToken token)
    {
        var data = await CaptureResourceBytesAsync(path, token).ConfigureAwait(false);
        return await Task.Run(() => ScdAudioDecoder.ResolveSoleAudioEntry(data), token)
            .ConfigureAwait(false);
    }

    public Task<byte[]> CaptureResourceBytesAsync(
        string path,
        CancellationToken token,
        bool logFailure = true)
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
            _ = ObserveDispatchAsync(dispatch, completion, logFailure);
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
            _ = ObserveDispatchAsync(Task.CompletedTask, completion, logFailure);
        }
        return completion.Task.WaitAsync(token);
    }

    private async Task ObserveDispatchAsync(
        Task dispatch, TaskCompletionSource<byte[]> completion, bool logFailure)
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
        if (logFailure && failure is not null && failure is not OperationCanceledException)
            log?.Warning(failure is AggregateException aggregate ? aggregate.GetBaseException() : failure,
                "SCD resource framework dispatch failed");
    }
}
