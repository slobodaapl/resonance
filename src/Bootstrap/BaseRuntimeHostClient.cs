using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Resonance.Audio;
using Resonance.Tts;

namespace Resonance.Bootstrap;

internal class BaseRuntimeHostException : InvalidOperationException
{
    internal BaseRuntimeHostException(string message, bool processMayBeRunning = false)
        : base(message) => ProcessMayBeRunning = processMayBeRunning;

    internal BaseRuntimeHostException(string message, bool processMayBeRunning, Exception inner)
        : base(message, inner) => ProcessMayBeRunning = processMayBeRunning;

    internal bool ProcessMayBeRunning { get; }
}

internal sealed class BaseRuntimeHostBusyException(string message) : BaseRuntimeHostException(message);

internal interface IBaseRuntimeHost : IAsyncDisposable
{
    bool IsReady { get; }
    bool ContextReady { get; }
    string? ActiveBackendId { get; }
    bool IsBusy { get; }
    Task StartAsync(string backendName, CancellationToken token);
    Task SwitchBackendAsync(string backendName, CancellationToken token);
    Task<VoiceReference> ExtractReferenceAsync(
        ReadOnlyMemory<float> samples, string transcript, CancellationToken token);
    Task SynthesizeAsync(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token);
    Task<IReadOnlyList<BackendBenchmarkMeasurement>> BenchmarkAsync(
        IReadOnlyList<BackendInfo> backends, CancellationToken token);
}

internal sealed class BaseRuntimeHostClient : IBaseRuntimeHost
{
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);
    private readonly string executablePath;
    private readonly string talkerPath;
    private readonly string codecPath;
    private readonly string runtimeDirectory;
    private readonly string referenceRoot;
    private readonly string modelRoot;
    private readonly object processGate = new();
    private readonly object pendingGate = new();
    private readonly SemaphoreSlim inputWriteGate = new(1, 1);
    private readonly CancellationTokenSource lifecycleCancellation = new();
    private readonly Dictionary<string, PendingOperation> pending = new(StringComparer.Ordinal);
    private Process? process;
    private HostJob? job;
    private Stream? input;
    private Stream? output;
    private Task? readerTask;
    private Task<string>? stderrTask;
    private TaskCompletionSource? ready;
    private TaskCompletionSource? startCompletion;
    private string? hostRoot;
    private int unavailable;
    private int disposed;
    private int extractionActive;
    private int synthesisActive;
    private bool contextReady;
    private string? activeBackendId;
    private long nextCommandSequence;
    private Task? disposeTask;

    internal BaseRuntimeHostClient(
        string executablePath, string talkerPath, string codecPath,
        string runtimeDirectory, string modelRoot, string referenceRoot)
    {
        this.executablePath = Path.GetFullPath(executablePath);
        this.talkerPath = Path.GetFullPath(talkerPath);
        this.codecPath = Path.GetFullPath(codecPath);
        this.runtimeDirectory = Path.GetFullPath(runtimeDirectory);
        this.modelRoot = Path.GetFullPath(modelRoot);
        this.referenceRoot = Path.GetFullPath(referenceRoot);
    }

    public bool IsReady
    {
        get
        {
            lock (processGate)
                return IsProcessAliveLocked();
        }
    }

    public bool IsBusy => Volatile.Read(ref extractionActive) != 0
                          || Volatile.Read(ref synthesisActive) != 0;

    public bool ContextReady
    {
        get { lock (processGate) return contextReady && IsProcessAliveLocked(); }
    }

    public string? ActiveBackendId
    {
        get
        {
            lock (processGate)
                return contextReady && IsProcessAliveLocked() ? activeBackendId : null;
        }
    }

    public async Task StartAsync(string backendName, CancellationToken token)
    {
        ThrowIfDisposed();
        if (!OperatingSystem.IsWindows())
            throw new BaseRuntimeHostException("Base runtime host requires Windows/Wine process containment");
        if (String.IsNullOrWhiteSpace(backendName))
            throw new ArgumentException("Base runtime host backend is required", nameof(backendName));

        TaskCompletionSource completion;
        var owner = false;
        lock (processGate)
        {
            if (Volatile.Read(ref unavailable) != 0 && (process is not null || job is not null))
                throw new BaseRuntimeHostException(
                    "Base runtime host retains an unconfirmed child; restart or cleanup is required",
                    processMayBeRunning: true);
            if (IsProcessAliveLocked())
                return;
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(BaseRuntimeHostClient));
            if (startCompletion is not null)
            {
                completion = startCompletion
                    ?? throw new InvalidOperationException("Base runtime host startup state was lost");
            }
            else
            {
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                startCompletion = completion;
                owner = true;
            }
        }

        if (!owner)
        {
            await completion.Task.WaitAsync(token).ConfigureAwait(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            token, lifecycleCancellation.Token);
        try
        {
            await StartOwnedAsync(backendName, linked.Token).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
            ObserveTask(completion.Task);
            throw;
        }
        finally
        {
            lock (processGate)
            {
                if (ReferenceEquals(startCompletion, completion)) startCompletion = null;
            }
        }
    }

    private async Task StartOwnedAsync(string backendName, CancellationToken token)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var root = Path.Combine(referenceRoot, "base-host-" + nonce);
        var preserveRoot = false;
        try
        {
        Directory.CreateDirectory(root);
        var requestPath = Path.Combine(root, "request.json");
        var ownerPath = Path.Combine(root, "owner.json");
        var pendingOwnerPath = ownerPath + ".pending";
        var permitPath = Path.Combine(root, "launch.ready");
        var permitTemporary = permitPath + ".part";
        var request = new BaseHostLaunchRequest(
            BaseHostProtocol.SchemaVersion,
            ReferenceExtractionProtocol.AbiVersion,
            runtimeDirectory,
            talkerPath,
            codecPath,
            backendName,
            runtimeDirectory,
            modelRoot,
            root,
            referenceRoot,
            nonce,
            Path.GetDirectoryName(executablePath)
                ?? throw new InvalidDataException("Base runtime host executable directory is missing"));
        BaseHostProtocol.ValidateLaunchRequest(request);
        ReferenceExtractionProtocol.ValidateTransientPath(requestPath, root, "host request path");
        ReferenceExtractionProtocol.ValidateTransientPath(ownerPath, root, "host owner path");
        ReferenceExtractionProtocol.ValidateTransientPath(pendingOwnerPath, root, "host pending owner path");
        ReferenceExtractionProtocol.ValidateTransientPath(permitPath, root, "host permit path");
        ReferenceExtractionProtocol.ValidateTransientPath(permitTemporary, root, "host permit temporary path");
        await File.WriteAllTextAsync(requestPath,
            System.Text.Json.JsonSerializer.Serialize(request, BaseHostProtocol.JsonOptions()), token)
            .ConfigureAwait(false);
        using (var parentProcess = Process.GetCurrentProcess())
        {
            var pendingOwner = new ReferenceExtractionOwnership(
                parentProcess.Id, parentProcess.StartTime.ToUniversalTime().Ticks, nonce);
            await File.WriteAllTextAsync(pendingOwnerPath,
                System.Text.Json.JsonSerializer.Serialize(pendingOwner), token).ConfigureAwait(false);
        }

        var hostJob = HostJob.Create();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(requestPath);
        var child = new Process { StartInfo = startInfo };
        try
        {
            if (!child.Start()) throw new BaseRuntimeHostException("Base runtime host did not start");
            // Publish both process and containment owner before any operation
            // whose failure could leave the child alive.  An uncertain
            // termination path must retain a tracked job/root until teardown
            // proves the child exited.
            lock (processGate)
            {
                process = child;
                job = hostJob;
                hostRoot = root;
                contextReady = false;
                activeBackendId = null;
                Volatile.Write(ref unavailable, 0);
            }
            if (Volatile.Read(ref disposed) != 0)
                throw new OperationCanceledException("Base runtime host shutdown started");
            var childInput = child.StandardInput.BaseStream;
            var childOutput = child.StandardOutput.BaseStream;
            lock (processGate)
            {
                input = childInput;
                output = childOutput;
            }
            if (!hostJob.TryAssign(child, out var assignmentError))
            {
                try { child.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    assignmentError += $"; fallback termination failed: {error.Message}";
                }
                throw new BaseRuntimeHostException(
                    $"Base runtime host could not be contained: {assignmentError}",
                    processMayBeRunning: !await WaitForExitAsync(child).ConfigureAwait(false));
            }

            var readySource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (processGate)
            {
                ready = readySource;
                Volatile.Write(ref unavailable, 0);
                readerTask = ReadLoopAsync(child.StandardOutput.BaseStream);
                stderrTask = ReadBoundedStderrAsync(child.StandardError);
            }

            await WaitForOwnerAsync(ownerPath, child, nonce, token).ConfigureAwait(false);
            await File.WriteAllTextAsync(permitTemporary, nonce, token).ConfigureAwait(false);
            File.Move(permitTemporary, permitPath, true);
            TryDeleteFile(pendingOwnerPath);
            await readySource.Task.WaitAsync(StartupWait, token).ConfigureAwait(false);
        }
        catch (Exception startupError)
        {
            Stream? cleanupInput;
            Stream? cleanupOutput;
            Task? cleanupReader;
            Task<string>? cleanupStderr;
            lock (processGate)
            {
                cleanupInput = input;
                cleanupOutput = output;
                cleanupReader = readerTask;
                cleanupStderr = stderrTask;
                ready?.TrySetException(new OperationCanceledException(
                    "Base runtime host startup cleanup canceled the child lifecycle."));
                Volatile.Write(ref unavailable, 1);
            }
            try { cleanupInput?.Dispose(); }
            catch { }
            try { cleanupOutput?.Dispose(); }
            catch { }
            await AwaitObservedBoundedAsync(cleanupReader).ConfigureAwait(false);
            await AwaitObservedBoundedAsync(cleanupStderr).ConfigureAwait(false);
            var stderrText = cleanupStderr is { IsCompletedSuccessfully: true }
                ? cleanupStderr.Result.Trim()
                : String.Empty;
            var terminationError = (Exception?)null;
            var terminated = false;
            try { terminated = await TerminateProcessAsync(child, hostJob).ConfigureAwait(false); }
            catch (Exception error) { terminationError = error; }
            if (!terminated)
            {
                preserveRoot = true;
                ScheduleTerminationReaper(child, hostJob, cleanupInput, cleanupOutput,
                    cleanupReader, cleanupStderr, root);
                Volatile.Write(ref unavailable, 1);
                throw new BaseRuntimeHostException(
                    "Base runtime host could not be terminated safely during startup",
                    processMayBeRunning: true, terminationError ?? new InvalidOperationException(
                        "The host process did not exit within the termination deadline"));
            }
            if (!await hostJob.DisposeConfirmedAsync().ConfigureAwait(false))
            {
                preserveRoot = true;
                lock (processGate)
                {
                    if (process is null || ReferenceEquals(process, child))
                    {
                        process = child;
                        input = cleanupInput;
                        output = cleanupOutput;
                        readerTask = cleanupReader;
                        stderrTask = cleanupStderr;
                        hostRoot = root;
                        job = hostJob;
                    }
                }
                ScheduleTerminationReaper(child, hostJob, cleanupInput, cleanupOutput,
                    cleanupReader, cleanupStderr, root);
                Volatile.Write(ref unavailable, 1);
                throw new BaseRuntimeHostException(
                    "Base runtime host job handle close was not confirmed",
                    processMayBeRunning: true);
            }
            var rootDeleted = TryDeleteHostRoot(root);
            lock (processGate)
            {
                if (ReferenceEquals(process, child))
                {
                    process = null;
                    input = null;
                    output = null;
                    ready = null;
                    job = null;
                    readerTask = null;
                    stderrTask = null;
                    hostRoot = rootDeleted ? null : root;
                    contextReady = false;
                    activeBackendId = null;
                    Volatile.Write(ref unavailable, rootDeleted ? 0 : 1);
                }
            }
            try { child.Dispose(); }
            catch { }
            if (!rootDeleted)
            {
                preserveRoot = true;
                ScheduleRootCleanupReaper(root);
                throw new BaseRuntimeHostException(
                    "Base runtime host transient root could not be removed after startup failure");
            }
            if (terminationError is not null) throw terminationError;
            if (!String.IsNullOrWhiteSpace(stderrText))
                throw new BaseRuntimeHostException(
                    $"Base runtime host failed during startup: {startupError.Message}; helper stderr: {stderrText}",
                    processMayBeRunning: false, startupError);
            throw;
        }
        }
        catch
        {
            if (!preserveRoot) TryDeleteHostRoot(root);
            throw;
        }
    }

    public async Task SwitchBackendAsync(string backendName, CancellationToken token)
    {
        await EnsureStartedAsync(backendName, token).ConfigureAwait(false);
        var response = await RequestAsync(BaseHostFrameKind.SwitchBackend,
            BaseHostProtocol.SerializePayload(backendName), null, token).ConfigureAwait(false);
        var state = BaseHostProtocol.DeserializePayload<BaseHostBackendState>(response.Payload);
        if (state.ContextReady && String.IsNullOrWhiteSpace(state.ActiveBackendId))
            throw new InvalidDataException("Base runtime host backend state is invalid");
        lock (processGate)
        {
            contextReady = state.ContextReady;
            activeBackendId = state.ActiveBackendId;
        }
    }

    public async Task<VoiceReference> ExtractReferenceAsync(
        ReadOnlyMemory<float> samples, string transcript, CancellationToken token)
    {
        ThrowIfDisposed();
        ValidateReferenceInput(samples, transcript);
        await EnsureStartedAsync(null, token).ConfigureAwait(false);
        var root = hostRoot ?? throw new BaseRuntimeHostException("Base runtime host root is unavailable");
        Interlocked.Exchange(ref extractionActive, 1);
        var requestId = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(root, requestId + ".f32");
        var temporary = inputPath + ".part";
        using var inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            token, lifecycleCancellation.Token);
        try
        {
            var bytes = new byte[checked(samples.Length * sizeof(float))];
            Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
            await File.WriteAllBytesAsync(temporary, bytes, inputLifetime.Token).ConfigureAwait(false);
            File.Move(temporary, inputPath, true);
            var payload = BaseHostProtocol.SerializePayload(new BaseHostExtractPayload(inputPath, transcript));
            // Native extraction is deliberately uncancellable once accepted by
            // the host.  The host remains responsive and later synthesis gets
            // BusyExtraction rather than waiting behind this call.
            var response = await RequestAsync(BaseHostFrameKind.Extract, payload, null, CancellationToken.None)
                .ConfigureAwait(false);
            var reference = BaseHostProtocol.DeserializePayload<BaseHostReferencePayload>(response.Payload);
            BaseHostProtocol.ValidateVoiceReferencePayload(reference, transcript);
            return new(reference.SpeakerEmbedding, reference.RvqCodes,
                reference.RvqLength, reference.Codebooks, reference.Transcript);
        }
        finally
        {
            if (!lifecycleCancellation.IsCancellationRequested)
            {
                TryDeleteFile(temporary);
                TryDeleteFile(inputPath);
            }
            Volatile.Write(ref extractionActive, 0);
        }
    }

    public async Task SynthesizeAsync(SynthesisRequest request, StreamingAudioBuffer sink,
        CancellationToken token)
    {
        ThrowIfDisposed();
        await EnsureStartedAsync(null, token).ConfigureAwait(false);
        Interlocked.Exchange(ref synthesisActive, 1);
        try
        {
            var reference = request.Reference is null ? null : new BaseHostReferencePayload(
                request.Reference.SpeakerEmbedding, request.Reference.RvqCodes,
                request.Reference.RvqLength, request.Reference.Codebooks, request.Reference.Transcript);
            if (reference is not null)
                BaseHostProtocol.ValidateVoiceReferencePayload(reference);
            var payload = BaseHostProtocol.SerializePayload(new BaseHostSynthesisPayload(
                request.Text, request.Language, reference, request.Instruction,
                request.Seed, request.MaxNewTokens));
            await RequestAsync(BaseHostFrameKind.Synthesize, payload, sink, token).ConfigureAwait(false);
            sink.Complete();
        }
        catch (Exception error)
        {
            sink.Complete(error);
            throw;
        }
        finally { Volatile.Write(ref synthesisActive, 0); }
    }

    public async Task<IReadOnlyList<BackendBenchmarkMeasurement>> BenchmarkAsync(
        IReadOnlyList<BackendInfo> backends, CancellationToken token)
    {
        await EnsureStartedAsync(null, token).ConfigureAwait(false);
        var payload = BaseHostProtocol.SerializePayload(new BaseHostBenchmarkPayload(
            backends.Select(value => value.Name).ToArray()));
        var response = await RequestAsync(BaseHostFrameKind.Benchmark, payload, null, token).ConfigureAwait(false);
        var report = BaseHostProtocol.DeserializePayload<BaseHostBenchmarkResponse>(response.Payload);
        if (report.Results is null
            || (report.ContextReady && String.IsNullOrWhiteSpace(report.ActiveBackendId)))
            throw new InvalidDataException("Base runtime host benchmark context state is invalid");
        lock (processGate)
        {
            contextReady = report.ContextReady;
            activeBackendId = report.ActiveBackendId;
        }
        var results = report.Results;
        foreach (var result in results)
        {
            if (result is null || String.IsNullOrWhiteSpace(result.BackendName)
                || (!result.Successful && String.IsNullOrWhiteSpace(result.Error))
                || (result.Successful
                    && (result.InitializationSeconds is null
                        || result.TimeToFirstAudioSeconds is null
                        || result.RealTimeFactor is null
                        || !double.IsFinite(result.InitializationSeconds.Value)
                        || !double.IsFinite(result.TimeToFirstAudioSeconds.Value)
                        || !double.IsFinite(result.RealTimeFactor.Value)))
                || (!result.Successful
                    && (result.TimeToFirstAudioSeconds is not null
                        || result.RealTimeFactor is not null
                        || (result.InitializationSeconds is not null
                            && !double.IsFinite(result.InitializationSeconds.Value)))))
                throw new InvalidDataException("Base runtime host benchmark result is invalid");
        }
        return results.Select(value => new BackendBenchmarkMeasurement(
            value.BackendName, value.Successful,
            value.InitializationSeconds, value.TimeToFirstAudioSeconds,
            value.RealTimeFactor, value.Error)).ToArray();
    }

    private async Task EnsureStartedAsync(string? backendName, CancellationToken token)
    {
        lock (processGate)
        {
            if (IsProcessAliveLocked()) return;
        }
        if (backendName is null)
            throw new BaseRuntimeHostException("Base runtime host is not initialized with a backend");
        await StartAsync(backendName, token).ConfigureAwait(false);
    }

    private async Task<BaseHostFrame> RequestAsync(
        BaseHostFrameKind kind, string payload, StreamingAudioBuffer? sink, CancellationToken token)
    {
        Stream inputStream;
        lock (processGate)
        {
            if (process is null)
                throw new BaseRuntimeHostException("Base runtime host is unavailable");
            inputStream = input ?? throw new BaseRuntimeHostException("Base runtime host input is unavailable");
            if (Volatile.Read(ref unavailable) != 0 || !IsProcessAliveLocked())
                throw new BaseRuntimeHostException("Base runtime host has exited");
        }
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<BaseHostFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            token, lifecycleCancellation.Token);
        var operation = new PendingOperation(completion, sink, operationCancellation);
        lock (pendingGate) pending.Add(requestId, operation);
        try
        {
            await inputWriteGate.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                var frame = new BaseHostFrame(BaseHostProtocol.SchemaVersion,
                    ReferenceExtractionProtocol.AbiVersion, kind, requestId, payload,
                    NextCommandSequence());
                await BaseHostProtocol.WriteFrameAsync(inputStream, frame, operation.Token)
                    .ConfigureAwait(false);
            }
            finally { inputWriteGate.Release(); }
            if (kind == BaseHostFrameKind.Extract)
                return await completion.Task.WaitAsync(operation.Token).ConfigureAwait(false);
            try { return await completion.Task.WaitAsync(operation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested
                                                     && !lifecycleCancellation.IsCancellationRequested)
            {
                if (kind is BaseHostFrameKind.Synthesize
                    or BaseHostFrameKind.Benchmark or BaseHostFrameKind.SwitchBackend)
                {
                    try
                    {
                        await SendCancelAsync(requestId, kind, lifecycleCancellation.Token)
                            .ConfigureAwait(false);
                        await completion.Task.WaitAsync(ShutdownWait).ConfigureAwait(false);
                    }
                    catch { }
                }
                throw;
            }
        }
        finally
        {
            lock (pendingGate) pending.Remove(requestId);
            operationCancellation.Dispose();
        }
    }

    private long NextCommandSequence()
    {
        var sequence = Interlocked.Increment(ref nextCommandSequence);
        if (sequence <= 0 || sequence == long.MaxValue)
            throw new BaseRuntimeHostException("Base runtime host command sequence exhausted; restart required");
        return sequence;
    }

    private bool IsProcessAliveLocked()
    {
        if (Volatile.Read(ref unavailable) != 0 || process is not { } child)
            return false;
        try
        {
            var alive = !child.HasExited;
            if (!alive) Volatile.Write(ref unavailable, 1);
            return alive;
        }
        catch (ObjectDisposedException)
        {
            Volatile.Write(ref unavailable, 1);
            return false;
        }
        catch (InvalidOperationException)
        {
            Volatile.Write(ref unavailable, 1);
            return false;
        }
        catch (Win32Exception)
        {
            Volatile.Write(ref unavailable, 1);
            return false;
        }
    }

    private async Task ReadLoopAsync(Stream stream)
    {
        try
        {
            while (true)
            {
                var frame = await BaseHostProtocol.ReadFrameAsync(stream, CancellationToken.None)
                    .ConfigureAwait(false);
                if (frame is null)
                {
                    FailReadLoop(new BaseRuntimeHostException(
                        "Base runtime host control channel reached clean EOF"));
                    return;
                }
                if (frame.Kind == BaseHostFrameKind.Ready)
                {
                    var readyState = BaseHostProtocol.DeserializePayload<BaseHostReadyPayload>(frame.Payload);
                    lock (processGate)
                    {
                        contextReady = readyState.ContextReady;
                        activeBackendId = readyState.ActiveBackendId;
                        ready?.TrySetResult();
                    }
                    continue;
                }
                PendingOperation? operation;
                lock (pendingGate) pending.TryGetValue(frame.RequestId, out operation);
                if (operation is null) continue;
                if (frame.Kind == BaseHostFrameKind.Audio)
                {
                    if (operation.Sink is null || operation.AudioRejected) continue;
                    var audio = BaseHostProtocol.DeserializePayload<BaseHostAudioPayload>(frame.Payload);
                    if (audio.SampleCount <= 0
                        || audio.SampleCount > BaseHostProtocol.MaximumAudioFrameBytes / sizeof(float))
                        throw new InvalidDataException("Base runtime host audio frame size is invalid");
                    var bytes = Convert.FromBase64String(audio.SamplesBase64);
                    if (bytes.Length > BaseHostProtocol.MaximumAudioFrameBytes
                        || bytes.Length != checked(audio.SampleCount * sizeof(float)))
                        throw new InvalidDataException("Base runtime host audio frame size is invalid");
                    var samples = new float[audio.SampleCount];
                    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                    try
                    {
                        if (!await operation.Sink.WriteAsync(samples, operation.Token)
                                .ConfigureAwait(false))
                            await RejectAudioAsync(frame.RequestId, operation).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    continue;
                }
                switch (frame.Kind)
                {
                    case BaseHostFrameKind.BusyExtraction:
                        operation.Completion.TrySetException(new BaseRuntimeHostBusyException(
                            BaseHostProtocol.DeserializePayload<HostError>(frame.Payload).Message));
                        break;
                    case BaseHostFrameKind.Failed:
                        var error = BaseHostProtocol.DeserializePayload<HostError>(frame.Payload);
                        if (error.ContextReady.HasValue || error.ActiveBackendId is not null)
                        {
                            lock (processGate)
                            {
                                contextReady = error.ContextReady ?? false;
                                activeBackendId = error.ActiveBackendId;
                            }
                        }
                        operation.Completion.TrySetException(error.Canceled
                            ? new OperationCanceledException(error.Message)
                            : new BaseRuntimeHostException(error.Message));
                        break;
                    case BaseHostFrameKind.Reference:
                    case BaseHostFrameKind.BenchmarkResult:
                    case BaseHostFrameKind.Completed:
                    case BaseHostFrameKind.Pong:
                    case BaseHostFrameKind.ShutdownAck:
                        if (frame.Kind is BaseHostFrameKind.Reference or BaseHostFrameKind.BenchmarkResult)
                            operation.Intermediate = frame;
                        if (frame.Kind is BaseHostFrameKind.Completed or BaseHostFrameKind.Pong
                            or BaseHostFrameKind.ShutdownAck)
                            operation.Completion.TrySetResult(operation.Intermediate ?? frame);
                        break;
                }
            }
        }
        catch (Exception error)
        {
            FailReadLoop(error);
        }
    }

    private void FailReadLoop(Exception error)
    {
        Volatile.Write(ref unavailable, 1);
        lock (pendingGate)
        {
            foreach (var operation in pending.Values)
            {
                operation.Completion.TrySetException(error);
                operation.Sink?.Complete(error);
            }
        }
        lock (processGate)
        {
            contextReady = false;
            activeBackendId = null;
            ready?.TrySetException(error);
        }
        try { lifecycleCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RejectAudioAsync(string requestId, PendingOperation operation)
    {
        if (!operation.TryRejectAudio()) return;
        var error = new InvalidOperationException(
            "Base runtime host audio sink rejected a frame; synthesis was canceled.");
        try { operation.Cancel(); }
        catch (ObjectDisposedException) { return; }
        operation.Completion.TrySetException(error);
        operation.Sink?.Complete(error);
        try
        {
            using var timeout = new CancellationTokenSource(ShutdownWait);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token, lifecycleCancellation.Token);
            await SendCancelAsync(requestId, BaseHostFrameKind.Synthesize, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifecycleCancellation.IsCancellationRequested) { }
        catch (Exception cancelError)
        {
            Volatile.Write(ref unavailable, 1);
            try { lifecycleCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            operation.Completion.TrySetException(new BaseRuntimeHostException(
                "Base runtime host cancellation could not be sent after audio backpressure",
                false, cancelError));
        }
    }

    private async Task SendCancelAsync(
        string targetRequestId, BaseHostFrameKind targetKind, CancellationToken token)
    {
        Stream inputStream;
        lock (processGate)
        {
            if (process is null)
                throw new BaseRuntimeHostException("Base runtime host is unavailable");
            inputStream = input ?? throw new BaseRuntimeHostException("Base runtime host input is unavailable");
            if (Volatile.Read(ref unavailable) != 0 || !IsProcessAliveLocked())
                throw new BaseRuntimeHostException("Base runtime host has exited");
        }
        var cancelId = Guid.NewGuid().ToString("N");
        await inputWriteGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var cancel = new BaseHostFrame(BaseHostProtocol.SchemaVersion,
                ReferenceExtractionProtocol.AbiVersion, BaseHostFrameKind.CancelOperation,
                cancelId, BaseHostProtocol.SerializePayload(
                    new BaseHostCancelPayload(targetRequestId, targetKind)), NextCommandSequence());
            await BaseHostProtocol.WriteFrameAsync(inputStream, cancel, token).ConfigureAwait(false);
        }
        finally { inputWriteGate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (processGate)
        {
            if (disposeTask is not null) return new ValueTask(disposeTask);
            Interlocked.Exchange(ref disposed, 1);
            lifecycleCancellation.Cancel();
            disposeTask = Task.Run(DisposeCoreAsync);
            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        FailPending(new OperationCanceledException("Base runtime host is shutting down."));
        lock (processGate) Volatile.Write(ref unavailable, 1);
        Task? startup;
        var startupCompleted = true;
        lock (processGate) startup = startCompletion?.Task;
        if (startup is not null)
        {
            try { await startup.WaitAsync(ShutdownWait).ConfigureAwait(false); }
            catch (TimeoutException) { startupCompleted = false; }
            catch { }
        }
        if (!startupCompleted)
        {
            Process? startupChild;
            HostJob? startupJob;
            Stream? startupInput;
            Stream? startupOutput;
            Task? startupReader;
            Task? startupStderr;
            string? startupRoot;
            lock (processGate)
            {
                startupChild = process;
                startupJob = job;
                startupInput = input;
                startupOutput = output;
                startupReader = readerTask;
                startupStderr = stderrTask;
                startupRoot = hostRoot;
                Volatile.Write(ref unavailable, 1);
            }
            if (startupChild is not null)
                ScheduleTerminationReaper(startupChild, startupJob, startupInput,
                    startupOutput, startupReader, startupStderr, startupRoot);
            throw new BaseRuntimeHostException(
                "Base runtime host startup did not quiesce during shutdown",
                processMayBeRunning: true);
        }
        Process? child;
        HostJob? hostJob;
        Stream? hostInput;
        Stream? hostOutput;
        Task? reader;
        Task<string>? stderr;
        string? root;
        lock (processGate)
        {
            child = process;
            hostJob = job;
            hostInput = input;
            hostOutput = output;
            reader = readerTask;
            stderr = stderrTask;
            root = hostRoot;
            process = null;
            input = null;
            output = null;
            job = null;
            ready = null;
            hostRoot = null;
            contextReady = false;
            activeBackendId = null;
            readerTask = null;
            stderrTask = null;
            Volatile.Write(ref unavailable, 1);
        }
        if (child is null)
        {
            var jobClosed = hostJob is null
                || await hostJob.DisposeConfirmedAsync().ConfigureAwait(false);
            if (!jobClosed)
            {
                lock (processGate)
                {
                    job = hostJob;
                    hostRoot = root;
                    Volatile.Write(ref unavailable, 1);
                }
                if (hostJob is not null) ScheduleJobCloseReaper(hostJob, root);
                throw new BaseRuntimeHostException(
                    "Base runtime host job handle close was not confirmed",
                    processMayBeRunning: true);
            }
            var rootDeleted = root is null || TryDeleteHostRoot(root);
            if (!rootDeleted)
            {
                lock (processGate)
                {
                    hostRoot = root;
                    Volatile.Write(ref unavailable, 1);
                }
                ScheduleRootCleanupReaper(root!);
            }
            inputWriteGate.Dispose();
            lifecycleCancellation.Dispose();
            return;
        }
        var terminated = false;
        try
        {
            if (!child.HasExited && hostInput is not null)
            {
                try
                {
                    await SendShutdownAsync(hostInput).ConfigureAwait(false);
                }
                catch { }
            }
            await AwaitObservedBoundedAsync(reader).ConfigureAwait(false);
            await AwaitObservedBoundedAsync(stderr).ConfigureAwait(false);
            terminated = await WaitForExitAsync(child).ConfigureAwait(false);
            if (!terminated)
                terminated = await TerminateProcessAsync(child, hostJob).ConfigureAwait(false);
            if (!terminated)
            {
                lock (processGate)
                {
                    process = child;
                    input = hostInput;
                    output = hostOutput;
                    job = hostJob;
                    ready = null;
                    hostRoot = root;
                    readerTask = reader;
                    stderrTask = stderr;
                }
                ScheduleTerminationReaper(child, hostJob, hostInput, hostOutput,
                    reader, stderr, root);
                var error = new BaseRuntimeHostException(
                    "Base runtime host did not exit within the termination deadline",
                    processMayBeRunning: true);
                FailPending(error);
                throw error;
            }
        }
        finally
        {
            if (terminated)
            {
                try { hostInput?.Dispose(); }
                catch { }
                try { hostOutput?.Dispose(); }
                catch { }
                await AwaitObservedBoundedAsync(reader).ConfigureAwait(false);
                await AwaitObservedBoundedAsync(stderr).ConfigureAwait(false);
                var jobClosed = hostJob is null
                    || await hostJob.DisposeConfirmedAsync().ConfigureAwait(false);
                if (!jobClosed)
                {
                    lock (processGate)
                    {
                        process = child;
                        input = hostInput;
                        output = hostOutput;
                        job = hostJob;
                        hostRoot = root;
                        readerTask = reader;
                        stderrTask = stderr;
                        Volatile.Write(ref unavailable, 1);
                    }
                    if (hostJob is not null)
                        ScheduleTerminationReaper(child, hostJob, hostInput, hostOutput,
                            reader, stderr, root);
                    throw new BaseRuntimeHostException(
                        "Base runtime host job handle close was not confirmed",
                        processMayBeRunning: true);
                }
                try { child.Dispose(); }
                catch { }
                var rootDeleted = root is null || TryDeleteHostRoot(root);
                if (!rootDeleted)
                {
                    lock (processGate)
                    {
                        hostRoot = root;
                        Volatile.Write(ref unavailable, 1);
                    }
                    ScheduleRootCleanupReaper(root!);
                }
                inputWriteGate.Dispose();
                lifecycleCancellation.Dispose();
            }
        }
    }

    private void ScheduleTerminationReaper(
        Process child, HostJob? hostJob, Stream? hostInput, Stream? hostOutput,
        Task? reader, Task? stderr, string? root)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    if (!await TerminateProcessAsync(child, hostJob).ConfigureAwait(false))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                        continue;
                    }
                    try { hostInput?.Dispose(); }
                    catch { }
                    try { hostOutput?.Dispose(); }
                    catch { }
                    await AwaitObservedBoundedAsync(reader).ConfigureAwait(false);
                    await AwaitObservedBoundedAsync(stderr).ConfigureAwait(false);
                    var rootDeleted = root is null || TryDeleteHostRoot(root);
                    lock (processGate)
                    {
                        if (ReferenceEquals(process, child))
                        {
                            process = null;
                            input = null;
                            output = null;
                            job = null;
                            ready = null;
                            hostRoot = rootDeleted ? null : root;
                            readerTask = null;
                            stderrTask = null;
                            contextReady = false;
                            activeBackendId = null;
                            Volatile.Write(ref unavailable,
                                rootDeleted && Volatile.Read(ref disposed) == 0 ? 0 : 1);
                        }
                    }
                    try { child.Dispose(); }
                    catch { }
                    if (!rootDeleted && root is not null)
                        ScheduleRootCleanupReaper(root);
                    if (Volatile.Read(ref disposed) != 0)
                    {
                        inputWriteGate.Dispose();
                        lifecycleCancellation.Dispose();
                    }
                    return;
                }
            }
            catch { }
        });
    }

    private void ScheduleJobCloseReaper(HostJob hostJob, string? root)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    if (!await hostJob.DisposeConfirmedAsync().ConfigureAwait(false))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                        continue;
                    }
                    var rootDeleted = root is null || TryDeleteHostRoot(root);
                    lock (processGate)
                    {
                        if (ReferenceEquals(job, hostJob))
                        {
                            job = null;
                            if (String.Equals(hostRoot, root, StringComparison.Ordinal))
                                hostRoot = rootDeleted ? null : root;
                            if (rootDeleted && Volatile.Read(ref disposed) == 0)
                                Volatile.Write(ref unavailable, 0);
                        }
                    }
                    if (Volatile.Read(ref disposed) != 0)
                    {
                        inputWriteGate.Dispose();
                        lifecycleCancellation.Dispose();
                    }
                    if (!rootDeleted && root is not null)
                        ScheduleRootCleanupReaper(root);
                    return;
                }
            }
            catch { }
        });
    }

    private void ScheduleRootCleanupReaper(string root)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    if (!TryDeleteHostRoot(root))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                        continue;
                    }

                    lock (processGate)
                    {
                        if (String.Equals(hostRoot, root, StringComparison.Ordinal))
                        {
                            hostRoot = null;
                            if (Volatile.Read(ref disposed) == 0)
                                Volatile.Write(ref unavailable, 0);
                        }
                    }
                    return;
                }
            }
            catch { }
        });
    }

    private async Task SendShutdownAsync(Stream hostInput)
    {
        using var timeout = new CancellationTokenSource(ShutdownWait);
        await inputWriteGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var frame = new BaseHostFrame(BaseHostProtocol.SchemaVersion,
                ReferenceExtractionProtocol.AbiVersion, BaseHostFrameKind.Shutdown,
                Guid.NewGuid().ToString("N"), BaseHostProtocol.SerializePayload(new { }),
                NextCommandSequence());
            await BaseHostProtocol.WriteFrameAsync(hostInput, frame, timeout.Token)
                .ConfigureAwait(false);
        }
        finally { inputWriteGate.Release(); }
    }

    private void FailPending(Exception error)
    {
        lock (pendingGate)
        {
            foreach (var operation in pending.Values)
            {
                try { operation.Cancel(); }
                catch (ObjectDisposedException) { }
                operation.Completion.TrySetException(error);
                operation.Sink?.Complete(error);
            }
        }
        lock (processGate) ready?.TrySetException(error);
    }

    private static async Task WaitForOwnerAsync(
        string path, Process child, string nonce, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + StartupWait;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (child.HasExited) throw new BaseRuntimeHostException("Base runtime host exited before ownership handshake");
            if (File.Exists(path))
            {
                try
                {
                    var owner = System.Text.Json.JsonSerializer.Deserialize<ReferenceExtractionOwnership>(
                        await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
                    if (owner is not null && owner.ProcessId == child.Id
                        && owner.ProcessStartUtcTicks == child.StartTime.ToUniversalTime().Ticks
                        && String.Equals(owner.RequestNonce, nonce, StringComparison.Ordinal)) return;
                }
                catch (JsonException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), token).ConfigureAwait(false);
        }
        throw new TimeoutException("Base runtime host ownership handshake timed out");
    }

    private static async Task<bool> WaitForExitAsync(Process child)
    {
        try
        {
            await child.WaitForExitAsync().WaitAsync(ShutdownWait).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (ObjectDisposedException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return false; }
    }

    private static async Task<string> ReadBoundedStderrAsync(StreamReader reader)
    {
        const int maximumCharacters = 64 * 1024;
        var buffer = new char[4096];
        var result = new System.Text.StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0) break;
            if (result.Length < maximumCharacters)
                result.Append(buffer, 0, Math.Min(read, maximumCharacters - result.Length));
        }
        return result.ToString();
    }

    private static async Task AwaitObservedBoundedAsync(Task? task)
    {
        if (task is null) return;
        try
        {
            await task.WaitAsync(ShutdownWait).ConfigureAwait(false);
        }
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

    private static async Task<bool> TerminateProcessAsync(Process child, HostJob? job)
    {
        var started = true;
        try { _ = child.Id; }
        catch (InvalidOperationException) { started = false; }
        if (!started)
        {
            return job is null || await job.DisposeConfirmedAsync().ConfigureAwait(false);
        }
        var assigned = job?.IsAssigned == true;
        if (job is not null)
        {
            try { job.TryTerminate(out _); }
            catch { }
        }
        try
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        catch (NotSupportedException) { }
        var exited = await WaitForExitAsync(child).ConfigureAwait(false);
        if (!assigned)
        {
            if (job is not null) await job.DisposeConfirmedAsync().ConfigureAwait(false);
            return false;
        }
        var jobClosed = job is null || await job.DisposeConfirmedAsync().ConfigureAwait(false);
        return exited && jobClosed;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(BaseRuntimeHostClient));
    }

    private static void ValidateReferenceInput(ReadOnlyMemory<float> samples, string transcript)
    {
        if (samples.Length == 0 || samples.Length > ReferenceExtractionProtocol.MaximumSamples)
            throw new InvalidDataException("Base reference PCM is invalid");
        if (String.IsNullOrWhiteSpace(transcript)
            || transcript.Length > ReferenceExtractionProtocol.MaximumTranscriptCharacters)
            throw new InvalidDataException("Base reference transcript is invalid");
        foreach (var sample in samples.Span)
            if (!float.IsFinite(sample)) throw new InvalidDataException("Base reference PCM is invalid");
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryDeleteHostRoot(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return true;
            if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) return false;
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (attributes.HasFlag(FileAttributes.Directory)) return false;
                File.Delete(path);
            }
            Directory.Delete(root, recursive: false);
            return !Directory.Exists(root);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed class PendingOperation(
        TaskCompletionSource<BaseHostFrame> completion, StreamingAudioBuffer? sink,
        CancellationTokenSource operationCancellation)
    {
        internal TaskCompletionSource<BaseHostFrame> Completion { get; } = completion;
        internal StreamingAudioBuffer? Sink { get; } = sink;
        internal CancellationToken Token => operationCancellation.Token;
        internal BaseHostFrame? Intermediate { get; set; }
        private int audioRejected;
        internal bool AudioRejected => Volatile.Read(ref audioRejected) != 0;

        internal bool TryRejectAudio() => Interlocked.Exchange(ref audioRejected, 1) == 0;

        internal void Cancel() => operationCancellation.Cancel();
    }

    private sealed record HostError(
        string Message,
        bool Canceled = false,
        bool? ContextReady = null,
        string? ActiveBackendId = null);

    private sealed class HostJob : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint KillOnClose = 0x00002000;
        private readonly object gate = new();
        private nint handle;
        private int assigned;

        private HostJob(nint handle) { this.handle = handle; }

        internal static HostJob Create()
        {
            var handle = CreateJobObjectW(0, null);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");
            var limits = new JobObjectExtendedLimitInfo
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnClose },
            };
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<JobObjectExtendedLimitInfo>());
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer,
                        (uint)Marshal.SizeOf<JobObjectExtendedLimitInfo>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject");
                return new HostJob(handle);
            }
            catch
            {
                CloseHandle(handle);
                throw;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        internal bool TryAssign(Process process, out string error)
        {
            lock (gate)
            {
                var current = handle;
                if (current != 0 && AssignProcessToJobObject(current, process.Handle))
                {
                    assigned = 1;
                    error = String.Empty;
                    return true;
                }
                error = current == 0
                    ? "AssignProcessToJobObject attempted after the job handle closed"
                    : $"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}";
                return false;
            }
        }

        internal bool TryTerminate(out string error)
        {
            lock (gate)
            {
                var current = handle;
                if (current != 0 && TerminateJobObject(current, 1))
                {
                    error = String.Empty;
                    return true;
                }
                error = $"TerminateJobObject failed: {Marshal.GetLastWin32Error()}";
                return false;
            }
        }

        internal bool IsAssigned
        {
            get { lock (gate) return assigned != 0; }
        }

        internal bool TryClose(out string error)
        {
            lock (gate)
            {
                if (handle == 0)
                {
                    error = String.Empty;
                    return true;
                }
                var current = handle;
                if (!CloseHandle(current))
                {
                    error = $"CloseHandle failed: {Marshal.GetLastWin32Error()}";
                    return false;
                }
                handle = 0;
                error = String.Empty;
                return true;
            }
        }

        internal async Task<bool> DisposeConfirmedAsync(CancellationToken token = default)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (TryClose(out _)) return true;
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return false; }
            }
            return false;
        }

        public void Dispose()
        {
            // Synchronous disposal never starts an unowned retry task.  A
            // failed close retains the handle for an explicit async owner.
            _ = TryClose(out _);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateJobObjectW(nint attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(nint job, int informationClass, nint information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(nint job, nint process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(nint job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(nint handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation { internal long PerProcessUserTimeLimit; internal long PerJobUserTimeLimit; internal uint LimitFlags; internal nuint MinimumWorkingSetSize; internal nuint MaximumWorkingSetSize; internal uint ActiveProcessLimit; internal nuint Affinity; internal uint PriorityClass; internal uint SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { internal ulong ReadOperationCount; internal ulong WriteOperationCount; internal ulong OtherOperationCount; internal ulong ReadTransferCount; internal ulong WriteTransferCount; internal ulong OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInfo { internal JobObjectBasicLimitInformation BasicLimitInformation; internal IoCounters IoInfo; internal nuint ProcessMemoryLimit; internal nuint JobMemoryLimit; internal nuint PeakProcessMemoryUsed; internal nuint PeakJobMemoryUsed; }
    }
}
