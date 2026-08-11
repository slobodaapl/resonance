using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;
using Resonance.Bootstrap;

namespace Resonance.ReferenceExtractor;

internal delegate bool AudioSender(string requestId, ReadOnlySpan<float> samples);

internal interface IBaseHostServerRuntime : IDisposable
{
    string BackendName { get; }
    bool IsTerminalPoisoned { get; }
    bool ContextReady { get; }
    string? ActiveBackendId { get; }
    bool ExtractionActive { get; }
    bool SynthesisActive { get; }
    bool TryBeginExtraction();
    void EndExtraction();
    bool TryBeginSynthesis(string requestId);
    void EndSynthesis(string requestId);
    void CancelSynthesis(string requestId);
    void CancelOperation(string requestId, BaseHostFrameKind targetKind);
    bool IsOperationCancellationRequested(string requestId);
    void ClearOperationCancellation(string requestId);
    void Shutdown();
    BaseHostReferencePayload Extract(BaseHostExtractPayload payload);
    void Synthesize(BaseHostSynthesisPayload payload, string requestId, AudioSender sendAudio);
    void SwitchBackend(string backend, string requestId);
    IReadOnlyList<BaseHostBenchmarkResult> Benchmark(
        IReadOnlyList<string> backends, string requestId);
}

internal sealed class BaseHostServer : IDisposable
{
    private const int CommandCapacity = 32;
    private const int AudioCapacity = 8;
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

    private sealed record AudioChunk(float[] Samples, int Count);
    private sealed record PendingCancellation(
        BaseHostFrameKind TargetKind, string CancellationRequestId, bool ResponseQueued);
    private sealed record DispatchReservation(
        string RequestId, BaseHostFrameKind Kind, bool OperationRegistered);

    private readonly IBaseHostServerRuntime runtime;
    private readonly Stream input;
    private readonly Stream output;
    private readonly Channel<BaseHostFrame> commands = Channel.CreateBounded<BaseHostFrame>(
        new BoundedChannelOptions(CommandCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly CancellationTokenSource shutdown = new();
    private readonly BaseHostResponseRouter responses;
    private readonly object dispatchGate = new();
    private readonly Dictionary<string, PendingCancellation> canceledOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BaseHostFrameKind> queuedOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BaseHostFrameKind> activeOperations = new(StringComparer.Ordinal);
    private readonly BaseHostCommandSequence commandSequence = new();
    private readonly Func<BaseHostFrame, Task>? beforeDispatch;
    private readonly Func<BaseHostFrame, Task>? afterReservation;
    private readonly Func<BaseHostFrame, Task>? afterCancellation;
    private Task? controlReader;
    private Task? outputWriter;
    private AudioStream? activeAudio;
    private int extractionQueued;
    private int stopRequested;
    private int shutdownStarted;
    private int outputFailed;
    private int disposed;

    internal BaseHostServer(
        IBaseHostServerRuntime runtime,
        Stream input,
        Stream output,
        Func<BaseHostFrame, Task>? beforeDispatch = null,
        Func<BaseHostFrame, Task>? afterReservation = null,
        Func<BaseHostFrame, Task>? afterCancellation = null)
    {
        this.runtime = runtime;
        this.input = input;
        this.output = output;
        this.beforeDispatch = beforeDispatch;
        this.afterReservation = afterReservation;
        this.afterCancellation = afterCancellation;
        responses = new BaseHostResponseRouter(64, AudioCapacity,
            error => ThreadPool.QueueUserWorkItem(_ =>
            {
                try { FailHost(error.Message); }
                catch { }
            }),
            error => activeAudio?.Cancel(error));
    }

    internal async Task RunAsync()
    {
        if (runtime.IsTerminalPoisoned)
        {
            FailHost("Base runtime host native ownership is poisoned; restart is required");
            return;
        }
        outputWriter = Task.Run(WriteOutputLoopAsync);
        if (!QueueControl(BaseHostFrameKind.Ready, Guid.NewGuid().ToString("N"),
                new BaseHostReadyPayload(runtime.BackendName, runtime.ContextReady,
                    runtime.ActiveBackendId)))
        {
            FailHost("Base runtime host could not queue its ready response");
            await AwaitBoundedAsync(outputWriter).ConfigureAwait(false);
            return;
        }
        controlReader = Task.Run(ReadControlLoopAsync);
        try
        {
            await foreach (var frame in commands.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
            {
                var reservation = await ReserveDispatchAsync(frame).ConfigureAwait(false);
                if (reservation is null)
                    continue;
                try
                {
                    if (afterReservation is not null)
                        await afterReservation(frame).ConfigureAwait(false);
                }
                catch
                {
                    if (reservation.OperationRegistered)
                        DeactivateOperation(reservation.RequestId, reservation.Kind);
                    throw;
                }
                if (frame.Kind == BaseHostFrameKind.Shutdown)
                {
                    QueueControl(BaseHostFrameKind.ShutdownAck, frame.RequestId, new { });
                    break;
                }
                await ProcessAsync(frame, reservation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        finally
        {
            activeAudio?.Cancel(new OperationCanceledException("Base runtime host is shutting down."));
            StopDispatch();
            commands.Writer.TryComplete();
            if (controlReader is not null)
                await AwaitBoundedAsync(controlReader).ConfigureAwait(false);
            responses.Complete();
            if (outputWriter is not null)
                await AwaitBoundedAsync(outputWriter).ConfigureAwait(false);
        }
    }

    private async Task<DispatchReservation?> ReserveDispatchAsync(BaseHostFrame frame)
    {
        if (beforeDispatch is not null)
            await beforeDispatch(frame).ConfigureAwait(false);

        BaseHostFrameKind? canceledKind = null;
        var mismatchedCancellation = false;
        var operation = frame.Kind is BaseHostFrameKind.Extract
            or BaseHostFrameKind.Synthesize
            or BaseHostFrameKind.Benchmark
            or BaseHostFrameKind.SwitchBackend;
        lock (dispatchGate)
        {
            if (stopRequested != 0)
                return null;
            queuedOperations.Remove(frame.RequestId);
            if (canceledOperations.Remove(frame.RequestId, out var requestedCancellation))
            {
                if (requestedCancellation.TargetKind == frame.Kind)
                {
                    if (frame.Kind == BaseHostFrameKind.Extract)
                        Volatile.Write(ref extractionQueued, 0);
                    canceledKind = requestedCancellation.TargetKind;
                    if (!requestedCancellation.ResponseQueued)
                        QueueControl(BaseHostFrameKind.Failed,
                            requestedCancellation.CancellationRequestId,
                            new { canceled = true, message = "Base operation cancellation accepted" });
                    QueueControl(BaseHostFrameKind.Failed, frame.RequestId,
                        new { canceled = true, message = "Base operation canceled before dispatch" });
                }
                else
                {
                    mismatchedCancellation = true;
                    QueueFailure(requestedCancellation.CancellationRequestId,
                        "Base operation cancellation target kind did not match the queued operation");
                    runtime.ClearOperationCancellation(frame.RequestId);
                }
            }
            if (!canceledKind.HasValue && operation)
            {
                runtime.ClearOperationCancellation(frame.RequestId);
                activeOperations[frame.RequestId] = frame.Kind;
            }
        }
        if (mismatchedCancellation)
            return new(frame.RequestId, frame.Kind, operation);
        if (canceledKind.HasValue)
        {
            runtime.ClearOperationCancellation(frame.RequestId);
            return null;
        }
        if (!operation)
        {
            runtime.ClearOperationCancellation(frame.RequestId);
            return new(frame.RequestId, frame.Kind, false);
        }
        return new(frame.RequestId, frame.Kind, true);
    }

    private static async Task AwaitBoundedAsync(Task task)
    {
        try { await task.WaitAsync(ShutdownWait).ConfigureAwait(false); }
        catch (TimeoutException) { ObserveTask(task); }
        catch { }
    }

    private static void ObserveTask(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ReadControlLoopAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var frame = await BaseHostProtocol.ReadFrameAsync(input, shutdown.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    ShutdownHost("Base runtime host input reached clean EOF", fatal: false);
                    return;
                }
                if (!AcceptCommandSequence(frame, out var sequenceError))
                {
                    QueueFailure(frame.RequestId,
                        sequenceError ?? "Duplicate or stale Base runtime host command sequence");
                    continue;
                }
                if (frame.Kind == BaseHostFrameKind.Shutdown)
                {
                    lock (dispatchGate)
                    {
                        if (stopRequested != 0) return;
                        QueueControl(BaseHostFrameKind.ShutdownAck, frame.RequestId, new { });
                    }
                    ShutdownHost("Base runtime host received shutdown", fatal: false);
                    return;
                }
                if (frame.Kind is BaseHostFrameKind.CancelSynthesis or BaseHostFrameKind.CancelOperation)
                {
                    HandleCancellation(frame);
                    if (afterCancellation is not null)
                        await afterCancellation(frame).ConfigureAwait(false);
                    continue;
                }
                if (frame.Kind == BaseHostFrameKind.Ping)
                {
                    QueueControl(BaseHostFrameKind.Pong, frame.RequestId, new { });
                    continue;
                }
                if (frame.Kind == BaseHostFrameKind.Synthesize
                    && (runtime.ExtractionActive || Volatile.Read(ref extractionQueued) != 0))
                {
                    QueueControl(BaseHostFrameKind.BusyExtraction, frame.RequestId,
                        new { message = "Base reference extraction is active" });
                    continue;
                }
                if (frame.Kind == BaseHostFrameKind.Extract && runtime.SynthesisActive)
                {
                    QueueControl(BaseHostFrameKind.BusyExtraction, frame.RequestId,
                        new { message = "Base synthesis is active" });
                    continue;
                }
                var markedExtraction = false;
                if (frame.Kind == BaseHostFrameKind.Extract)
                {
                    if (runtime.ExtractionActive || runtime.SynthesisActive
                        || Interlocked.CompareExchange(ref extractionQueued, 1, 0) != 0)
                    {
                        QueueControl(BaseHostFrameKind.BusyExtraction, frame.RequestId,
                            new { message = "Base runtime is busy" });
                        continue;
                    }
                    markedExtraction = true;
                }
                try
                {
                    var queued = false;
                    lock (dispatchGate)
                    {
                        if (stopRequested == 0 && commands.Writer.TryWrite(frame))
                        {
                            queuedOperations[frame.RequestId] = frame.Kind;
                            queued = true;
                        }
                    }
                    if (!queued)
                    {
                        if (markedExtraction) Volatile.Write(ref extractionQueued, 0);
                        QueueFailure(frame.RequestId,
                            "Base runtime host command queue is full; retry later");
                    }
                }
                catch
                {
                    lock (dispatchGate) queuedOperations.Remove(frame.RequestId);
                    if (markedExtraction) Volatile.Write(ref extractionQueued, 0);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (EndOfStreamException) { ShutdownHost("Base runtime host input reached EOF", fatal: false); }
        catch (Exception error) { FailHost(error.Message); }
        finally
        {
            StopDispatch();
            commands.Writer.TryComplete();
        }
    }

    private void HandleCancellation(BaseHostFrame frame)
    {
        BaseHostCancelPayload cancellation;
        try
        {
            cancellation = BaseHostProtocol.DeserializePayload<BaseHostCancelPayload>(frame.Payload);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            QueueFailure(frame.RequestId, $"Base operation cancellation payload is invalid: {error.Message}");
            return;
        }
        if (!Guid.TryParseExact(cancellation.TargetRequestId, "N", out _))
        {
            QueueFailure(frame.RequestId, "Base operation cancellation target is invalid");
            return;
        }
        if (frame.Kind == BaseHostFrameKind.CancelSynthesis
            && cancellation.TargetKind != BaseHostFrameKind.Synthesize)
        {
            QueueFailure(frame.RequestId,
                "Legacy synthesis cancellation cannot target another Base operation kind");
            return;
        }
        var targetKind = frame.Kind == BaseHostFrameKind.CancelSynthesis
            ? BaseHostFrameKind.Synthesize
            : cancellation.TargetKind;
        if (targetKind is not (BaseHostFrameKind.Extract or BaseHostFrameKind.Synthesize
            or BaseHostFrameKind.Benchmark or BaseHostFrameKind.SwitchBackend))
        {
            QueueFailure(frame.RequestId, "Base operation cancellation target kind is invalid");
            return;
        }
        var active = false;
        var mismatch = false;
        lock (dispatchGate)
        {
            if (stopRequested != 0)
                return;
            if (activeOperations.TryGetValue(cancellation.TargetRequestId, out var activeKind))
            {
                mismatch = activeKind != targetKind;
                active = !mismatch;
            }
            else if (queuedOperations.TryGetValue(cancellation.TargetRequestId, out var queuedKind))
            {
                mismatch = queuedKind != targetKind;
                if (!mismatch && !canceledOperations.ContainsKey(cancellation.TargetRequestId))
                {
                    canceledOperations[cancellation.TargetRequestId] =
                        new PendingCancellation(targetKind, frame.RequestId, true);
                    if (targetKind == BaseHostFrameKind.Extract
                        && Volatile.Read(ref extractionQueued) != 0)
                        Volatile.Write(ref extractionQueued, 0);
                    QueueControl(BaseHostFrameKind.Failed, frame.RequestId,
                        new { canceled = true, message = "Base operation cancellation accepted" });
                }
            }
            else if (canceledOperations.TryGetValue(
                         cancellation.TargetRequestId, out var priorCancellation))
            {
                mismatch = priorCancellation.TargetKind != targetKind;
            }
            else
            {
                // Publish the typed cancellation before touching the runtime.
                // ReserveDispatch consumes this entry only for the matching
                // queued operation, so a later operation cannot be resurrected
                // by an early runtime cancellation clear.
                canceledOperations[cancellation.TargetRequestId] =
                    new PendingCancellation(targetKind, frame.RequestId, false);
                if (targetKind == BaseHostFrameKind.Extract
                    && Volatile.Read(ref extractionQueued) != 0)
                    Volatile.Write(ref extractionQueued, 0);
            }
            if (mismatch)
            {
                QueueFailure(frame.RequestId,
                    "Base operation cancellation target kind did not match the active or queued operation");
                return;
            }
            if (targetKind == BaseHostFrameKind.Extract && active)
            {
                QueueControl(BaseHostFrameKind.Failed, frame.RequestId,
                    new { canceled = false, notCancelable = true,
                        message = "Active Base reference extraction continues; cancellation is not supported." });
            }
            else if (active)
            {
                QueueControl(BaseHostFrameKind.Failed, frame.RequestId,
                    new { canceled = true, message = "Base operation cancellation accepted" });
            }
        }
        if (active && targetKind != BaseHostFrameKind.Extract)
        {
            var canceledByAudio = targetKind == BaseHostFrameKind.Synthesize
                                  && activeAudio?.CancelIfMatches(
                                      cancellation.TargetRequestId) == true;
            if (!canceledByAudio)
                runtime.CancelOperation(cancellation.TargetRequestId, targetKind);
        }
    }

    private bool AcceptCommandSequence(BaseHostFrame frame, out string? error)
    {
        if (!commandSequence.TryAccept(frame.Sequence))
        {
            error = "Duplicate or stale Base runtime host command sequence";
            return false;
        }
        error = null;
        return true;
    }

    private void QueueFailure(string requestId, string message) =>
        QueueControl(BaseHostFrameKind.Failed, requestId,
            new
            {
                canceled = false,
                message,
                contextReady = runtime.ContextReady,
                activeBackendId = runtime.ActiveBackendId,
            });

    private void DeactivateOperation(string requestId, BaseHostFrameKind kind)
    {
        lock (dispatchGate)
        {
            if (activeOperations.TryGetValue(requestId, out var activeKind)
                && activeKind == kind)
                activeOperations.Remove(requestId);
        }
    }

    private async Task ProcessAsync(BaseHostFrame frame, DispatchReservation reservation)
    {
        try
        {
            if (frame.Kind is (BaseHostFrameKind.Extract
                or BaseHostFrameKind.Synthesize
                or BaseHostFrameKind.Benchmark
                or BaseHostFrameKind.SwitchBackend)
                && (!reservation.OperationRegistered
                    || !String.Equals(reservation.RequestId, frame.RequestId,
                        StringComparison.Ordinal)
                    || reservation.Kind != frame.Kind))
                throw new InvalidOperationException("Base operation reservation is invalid");
            switch (frame.Kind)
            {
                case BaseHostFrameKind.Ping:
                    QueueControl(BaseHostFrameKind.Pong, frame.RequestId, new { });
                    break;
                case BaseHostFrameKind.Extract:
                    Volatile.Write(ref extractionQueued, 0);
                    var extractionStarted = false;
                    try
                    {
                        if (runtime.IsOperationCancellationRequested(frame.RequestId))
                        {
                            QueueControl(BaseHostFrameKind.Failed, frame.RequestId,
                                new { canceled = true, message = "Base extraction canceled before start" });
                            break;
                        }
                        if (!runtime.TryBeginExtraction())
                        {
                            QueueControl(BaseHostFrameKind.BusyExtraction, frame.RequestId,
                                new { message = "Base reference extraction is active" });
                            break;
                        }
                        extractionStarted = true;
                        var payload = BaseHostProtocol.DeserializePayload<BaseHostExtractPayload>(frame.Payload);
                        var reference = runtime.Extract(payload);
                        if (!QueueControl(BaseHostFrameKind.Reference, frame.RequestId, reference)) break;
                        QueueControl(BaseHostFrameKind.Completed, frame.RequestId, new { });
                    }
                    finally
                    {
                        DeactivateOperation(frame.RequestId, BaseHostFrameKind.Extract);
                        if (extractionStarted) runtime.EndExtraction();
                        runtime.ClearOperationCancellation(frame.RequestId);
                    }
                    break;
                case BaseHostFrameKind.Synthesize:
                    var synthesisStarted = false;
                    AudioStream? audio = null;
                    try
                    {
                        if (runtime.IsOperationCancellationRequested(frame.RequestId))
                        {
                            QueueControl(BaseHostFrameKind.Failed, frame.RequestId,
                                new { canceled = true, message = "Base synthesis canceled before start" });
                            break;
                        }
                        if (!runtime.TryBeginSynthesis(frame.RequestId))
                        {
                            QueueControl(BaseHostFrameKind.BusyExtraction, frame.RequestId,
                                new { message = "Base synthesis is already active" });
                            break;
                        }
                        synthesisStarted = true;
                        var payload = BaseHostProtocol.DeserializePayload<BaseHostSynthesisPayload>(frame.Payload);
                        audio = new AudioStream(this, frame.RequestId, AudioCapacity);
                        activeAudio = audio;
                        runtime.Synthesize(payload, frame.RequestId, audio.TryWrite);
                        await audio.CompleteAsync().ConfigureAwait(false);
                        if (runtime.IsOperationCancellationRequested(frame.RequestId))
                            throw new OperationCanceledException("Base synthesis canceled.");
                        QueueControl(BaseHostFrameKind.Completed, frame.RequestId, new { });
                    }
                    catch (OperationCanceledException)
                    {
                        QueueControl(BaseHostFrameKind.Failed, frame.RequestId,
                            new { canceled = true, message = "Base synthesis canceled" });
                    }
                    finally
                    {
                        if (ReferenceEquals(activeAudio, audio)) activeAudio = null;
                        if (audio is not null) await audio.DisposeAsync().ConfigureAwait(false);
                        DeactivateOperation(frame.RequestId, BaseHostFrameKind.Synthesize);
                        if (synthesisStarted) runtime.EndSynthesis(frame.RequestId);
                        runtime.ClearOperationCancellation(frame.RequestId);
                    }
                    break;
                case BaseHostFrameKind.SwitchBackend:
                    try
                    {
                        if (runtime.IsOperationCancellationRequested(frame.RequestId))
                            throw new OperationCanceledException("Base backend switch canceled.");
                        var backend = BaseHostProtocol.DeserializePayload<string>(frame.Payload);
                        runtime.SwitchBackend(backend, frame.RequestId);
                        if (runtime.IsOperationCancellationRequested(frame.RequestId))
                            throw new OperationCanceledException("Base backend switch canceled.");
                        QueueControl(BaseHostFrameKind.Completed, frame.RequestId,
                            new BaseHostBackendState(runtime.ContextReady,
                                runtime.ActiveBackendId));
                    }
                    finally
                    {
                        DeactivateOperation(frame.RequestId, BaseHostFrameKind.SwitchBackend);
                        runtime.ClearOperationCancellation(frame.RequestId);
                    }
                    break;
                case BaseHostFrameKind.Benchmark:
                    try
                    {
                        if (runtime.IsOperationCancellationRequested(frame.RequestId))
                            throw new OperationCanceledException("Base benchmark canceled.");
                        var benchmark = BaseHostProtocol.DeserializePayload<BaseHostBenchmarkPayload>(frame.Payload);
                        if (benchmark.BackendNames is null || benchmark.BackendNames.Count == 0
                            || benchmark.BackendNames.Any(String.IsNullOrWhiteSpace))
                            throw new InvalidDataException("Base benchmark backend list is invalid");
                        var results = runtime.Benchmark(benchmark.BackendNames, frame.RequestId);
                        var report = new BaseHostBenchmarkResponse(results, runtime.ContextReady,
                            runtime.ActiveBackendId);
                        if (!QueueControl(BaseHostFrameKind.BenchmarkResult, frame.RequestId, report)) break;
                        QueueControl(BaseHostFrameKind.Completed, frame.RequestId, new { });
                    }
                    finally
                    {
                        DeactivateOperation(frame.RequestId, BaseHostFrameKind.Benchmark);
                        runtime.ClearOperationCancellation(frame.RequestId);
                    }
                    break;
                default:
                    throw new InvalidDataException($"Unsupported Base runtime host operation: {frame.Kind}");
            }
        }
        catch (OperationCanceledException error)
        {
            if (runtime.IsTerminalPoisoned)
            {
                FailHost("Base runtime host native ownership is poisoned; restart is required");
                throw new InvalidOperationException(
                    "Base runtime host native ownership is poisoned; restart is required", error);
            }
            QueueControl(BaseHostFrameKind.Failed, frame.RequestId,
                new { canceled = true, message = "Base operation canceled" });
        }
        catch (Exception error)
        {
            if (runtime.IsTerminalPoisoned)
            {
                FailHost("Base runtime host native ownership is poisoned; restart is required");
                throw;
            }
            QueueFailure(frame.RequestId, error.Message);
        }
        finally
        {
            if (reservation.OperationRegistered)
                DeactivateOperation(reservation.RequestId, reservation.Kind);
            runtime.ClearOperationCancellation(frame.RequestId);
        }
    }

    private bool QueueControl<T>(BaseHostFrameKind kind, string requestId, T payload) =>
        QueueControl(new BaseHostFrame(BaseHostProtocol.SchemaVersion,
            ReferenceExtractionProtocol.AbiVersion, kind, requestId,
            BaseHostProtocol.SerializePayload(payload)));

    private bool QueueControl(BaseHostFrame frame)
    {
        if (Volatile.Read(ref outputFailed) != 0) return false;
        return responses.TryQueueControl(frame);
    }

    private bool QueueAudio(string requestId, float[] samples, int count)
    {
        var bytes = new byte[checked(count * sizeof(float))];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        var payload = BaseHostProtocol.SerializePayload(new BaseHostAudioPayload(
            Convert.ToBase64String(bytes), count));
        return responses.TryQueueAudio(new BaseHostFrame(BaseHostProtocol.SchemaVersion,
            ReferenceExtractionProtocol.AbiVersion, BaseHostFrameKind.Audio,
            requestId, payload));
    }

    private async Task WriteOutputLoopAsync()
    {
        try
        {
            while (true)
            {
                var frame = await responses.ReadNextAsync(shutdown.Token).ConfigureAwait(false);
                if (frame is null) break;
                await BaseHostProtocol.WriteFrameAsync(output, frame, shutdown.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error) { FailHost(error.Message); }
    }

    private void FailHost(string message) => ShutdownHost(message, fatal: true);

    private void StopDispatch()
    {
        lock (dispatchGate)
            stopRequested = 1;
    }

    private void ShutdownHost(string message, bool fatal)
    {
        lock (dispatchGate)
        {
            if (shutdownStarted != 0) return;
            stopRequested = 1;
            shutdownStarted = 1;
        }
        Exception? shutdownError = null;
        try { runtime.Shutdown(); }
        catch (Exception error) { shutdownError = error; }
        try { activeAudio?.Cancel(new InvalidOperationException(message, shutdownError)); }
        catch (Exception error) { shutdownError ??= error; }
        if (runtime.IsTerminalPoisoned)
            fatal = true;
        lock (dispatchGate)
        {
            if (fatal || shutdownError is not null)
            {
                Volatile.Write(ref outputFailed, 1);
                var error = new InvalidOperationException(message, shutdownError);
                commands.Writer.TryComplete(error);
                responses.Fail(error);
            }
            else
            {
                commands.Writer.TryComplete();
                responses.Complete();
            }
        }
        if (fatal || shutdownError is not null)
        {
            try { shutdown.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private sealed class AudioStream : IAsyncDisposable
    {
        private enum TerminalState
        {
            Open,
            Completed,
            Failed,
            Canceled,
            Disposed,
        }

        private readonly BaseHostServer owner;
        private readonly string requestId;
        private readonly Channel<AudioChunk> chunks;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task writer;
        private readonly object disposeGate = new();
        private Exception? failure;
        private int terminalState;
        private Task? disposeTask;

        internal AudioStream(BaseHostServer owner, string requestId, int capacity)
        {
            this.owner = owner;
            this.requestId = requestId;
            chunks = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
            writer = Task.Run(WriteLoopAsync);
        }

        internal bool TryWrite(string _, ReadOnlySpan<float> samples)
        {
            if (samples.IsEmpty) return true;
            if (Volatile.Read(ref terminalState) != (int)TerminalState.Open
                || cancellation.IsCancellationRequested)
                return false;
            var maximumSamples = BaseHostProtocol.MaximumAudioFrameBytes / sizeof(float);
            for (var offset = 0; offset < samples.Length; offset += maximumSamples)
            {
                var count = Math.Min(maximumSamples, samples.Length - offset);
                var rented = ArrayPool<float>.Shared.Rent(count);
                samples.Slice(offset, count).CopyTo(rented);
                if (chunks.Writer.TryWrite(new AudioChunk(rented, count))) continue;
                ArrayPool<float>.Shared.Return(rented);
                Fail(new InvalidOperationException(
                    "Base runtime host audio backpressure limit was exceeded."));
                return false;
            }
            return true;
        }

        internal void Cancel(Exception error) => Fail(error);

        internal bool CancelIfMatches(string requestId)
        {
            if (String.Equals(this.requestId, requestId, StringComparison.Ordinal))
            {
                Cancel(new OperationCanceledException("Base synthesis canceled."));
                return true;
            }
            return false;
        }

        internal async Task CompleteAsync()
        {
            Interlocked.CompareExchange(ref terminalState,
                (int)TerminalState.Completed, (int)TerminalState.Open);
            chunks.Writer.TryComplete();
            try { await writer.ConfigureAwait(false); }
            catch (Exception error) { Fail(error); }
            if (failure is not null) throw failure;
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                await foreach (var chunk in chunks.Reader.ReadAllAsync(cancellation.Token)
                    .ConfigureAwait(false))
                {
                    try
                    {
                        if (!owner.QueueAudio(requestId, chunk.Samples, chunk.Count))
                        {
                            Fail(new InvalidOperationException(
                                "Base runtime host output audio queue is full."));
                            return;
                        }
                    }
                    finally { ArrayPool<float>.Shared.Return(chunk.Samples); }
                }
            }
            catch (Exception error)
            {
                Fail(error);
            }
            finally
            {
                while (chunks.Reader.TryRead(out var chunk))
                    ArrayPool<float>.Shared.Return(chunk.Samples);
            }
        }

        private void Fail(Exception error)
        {
            Interlocked.CompareExchange(ref failure, error, null);
            var terminal = error is OperationCanceledException
                ? TerminalState.Canceled
                : TerminalState.Failed;
            if (Interlocked.CompareExchange(ref terminalState, (int)terminal,
                    (int)TerminalState.Open) != (int)TerminalState.Open)
                return;
            try { owner.runtime.CancelOperation(requestId, BaseHostFrameKind.Synthesize); }
            catch (Exception cancelError) { Interlocked.CompareExchange(ref failure, cancelError, null); }
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            chunks.Writer.TryComplete(error);
        }

        public ValueTask DisposeAsync()
        {
            Task task;
            lock (disposeGate)
                task = disposeTask ??= DisposeCoreAsync();
            return new(task);
        }

        private async Task DisposeCoreAsync()
        {
            if (Interlocked.CompareExchange(ref terminalState,
                    (int)TerminalState.Disposed, (int)TerminalState.Open)
                == (int)TerminalState.Open)
            {
                var error = new OperationCanceledException("Base audio stream disposed.");
                Interlocked.CompareExchange(ref failure, error, null);
                try { owner.runtime.CancelOperation(requestId, BaseHostFrameKind.Synthesize); }
                catch (Exception cancelError)
                {
                    Interlocked.CompareExchange(ref failure, cancelError, null);
                }
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                chunks.Writer.TryComplete(error);
            }
            try { await writer.ConfigureAwait(false); }
            catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
            while (chunks.Reader.TryRead(out var chunk))
                ArrayPool<float>.Shared.Return(chunk.Samples);
            cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        ShutdownHost("Base runtime host is shutting down.", fatal: false);
        try { shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        if ((controlReader is null || controlReader.IsCompleted)
            && (outputWriter is null || outputWriter.IsCompleted))
            shutdown.Dispose();
        else
        {
            if (controlReader is not null) ObserveTask(controlReader);
            if (outputWriter is not null) ObserveTask(outputWriter);
        }
    }
}
