using System.Threading.Channels;

namespace Resonance.Bootstrap;

// Shared transport core.  The helper owns the actual pipe writer; this type
// only owns bounded response admission and its control-before-audio ordering.
internal sealed class BaseHostResponseRouter
{
    private readonly Channel<BaseHostFrame> control;
    private readonly Channel<BaseHostFrame> audio;
    private readonly Action<Exception>? failure;
    private readonly Action<Exception>? audioFailure;
    private Task<bool>? controlWait;
    private Task<bool>? audioWait;
    private bool controlClosed;
    private bool audioClosed;
    private int failed;

    internal BaseHostResponseRouter(int controlCapacity, int audioCapacity,
        Action<Exception>? failure = null, Action<Exception>? audioFailure = null)
    {
        if (controlCapacity <= 0 || audioCapacity <= 0)
            throw new ArgumentOutOfRangeException();
        control = CreateChannel(controlCapacity);
        audio = CreateChannel(audioCapacity);
        this.failure = failure;
        this.audioFailure = audioFailure;
    }

    internal bool IsFailed => Volatile.Read(ref failed) != 0;

    internal bool TryQueueControl(BaseHostFrame frame)
    {
        if (IsFailed) return false;
        if (control.Writer.TryWrite(frame)) return true;
        Fail(new InvalidOperationException("Base runtime host control response queue is full."));
        return false;
    }

    internal bool TryQueueAudio(BaseHostFrame frame)
    {
        if (IsFailed) return false;
        if (audio.Writer.TryWrite(frame)) return true;
        audioFailure?.Invoke(new InvalidOperationException(
            "Base runtime host audio response queue is full."));
        return false;
    }

    internal bool TryReadControl(out BaseHostFrame frame)
    {
        if (control.Reader.TryRead(out var value))
        {
            frame = value;
            return true;
        }
        frame = null!;
        return false;
    }

    internal bool TryReadAudio(out BaseHostFrame frame)
    {
        if (audio.Reader.TryRead(out var value))
        {
            frame = value;
            return true;
        }
        frame = null!;
        return false;
    }

    internal void Complete(Exception? error = null)
    {
        control.Writer.TryComplete(error);
        audio.Writer.TryComplete(error);
    }

    internal void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref failed, 1) != 0) return;
        Complete(error);
        failure?.Invoke(error);
    }

    internal async ValueTask<BaseHostFrame?> ReadNextAsync(CancellationToken token)
    {
        while (true)
        {
            if (TryReadControl(out var controlFrame)) return controlFrame;
            if (TryReadAudio(out var audioFrame)) return audioFrame;
            if (controlClosed && audioClosed)
                return null;
            if (!controlClosed)
                controlWait ??= control.Reader.WaitToReadAsync(token).AsTask();
            if (!audioClosed)
                audioWait ??= audio.Reader.WaitToReadAsync(token).AsTask();
            var waits = new List<Task<bool>>(2);
            if (controlWait is not null) waits.Add(controlWait);
            if (audioWait is not null) waits.Add(audioWait);
            await Task.WhenAny(waits).ConfigureAwait(false);
            if (controlWait?.IsCompleted == true)
            {
                var ready = await controlWait.ConfigureAwait(false);
                controlWait = null;
                if (!ready) controlClosed = true;
                if (ready) continue;
            }
            if (audioWait?.IsCompleted == true)
            {
                var ready = await audioWait.ConfigureAwait(false);
                audioWait = null;
                if (!ready) audioClosed = true;
                if (ready) continue;
            }
        }
    }

    private static Channel<BaseHostFrame> CreateChannel(int capacity) =>
        Channel.CreateBounded<BaseHostFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
}
