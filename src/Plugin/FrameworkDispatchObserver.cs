using Dalamud.Plugin.Services;

namespace Resonance.Plugin;

internal static class FrameworkDispatchObserver
{
    public static async Task AwaitAsync(
        Task dispatch, CancellationToken token, IPluginLog log, string failureMessage)
    {
        try
        {
            await dispatch.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = ObserveAfterCancellationAsync(dispatch, log, failureMessage);
            throw;
        }
    }

    private static async Task ObserveAfterCancellationAsync(
        Task dispatch, IPluginLog log, string failureMessage)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            log.Warning(error is AggregateException aggregate ? aggregate.GetBaseException() : error,
                failureMessage);
        }
    }
}
