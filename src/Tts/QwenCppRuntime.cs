using System.Runtime.InteropServices;
using Resonance.Audio;
using Resonance.Plugin;

namespace Resonance.Tts;

internal sealed class QwenNativeException(string operation, int status, string? detail)
    : InvalidOperationException($"{operation} failed ({status}): {detail}")
{
    public string Operation { get; } = operation;
    public int Status { get; } = status;
}

public sealed class QwenCppRuntime : ITtsRuntime
{
    private sealed record CallbackState(StreamingAudioBuffer Sink, CancellationToken Token);

    private static readonly unsafe QwenNative.CancelCallback CancelThunk = OnCancel;
    private static readonly unsafe QwenNative.AudioChunkCallback ChunkThunk = OnChunk;
    private nint context;
    private static readonly object BackendPathGate = new();
    private static readonly HashSet<string> ConfiguredBackendPaths = new(StringComparer.OrdinalIgnoreCase);
    private static string? nativeRuntimeDirectory;
    private static int resolverInstalled;
    private static nint ggmlBaseHandle;
    private static nint ggmlHandle;
    private static nint qwenHandle;
    private static int nativeLibrariesReleased;
    private static int nativeLibrariesReleasing;
    private static Exception? nativeReleaseTerminalFailure;
    private static readonly object ProcessLeaseGate = new();
    private static Semaphore? processLease;
    private static int processLeaseUsers;
    private static int processOwnerUsers;
    private readonly object operationGate = new();
    private TaskCompletionSource? operationsIdle;
    private TaskCompletionSource? disposalCompletion;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private int activeOperations;
    private int disposing;
    private int terminalDisposalFailure;
    private int leaseReleased;
    private readonly bool ownsProcessLease;
    private readonly IProcessLifetimeLease? pluginLifetimeLease;

    public RuntimeCapabilities Capabilities { get; }
    public bool HasTerminalDisposalFailure => Volatile.Read(ref terminalDisposalFailure) != 0;
    public static bool HasNativeReleaseFailure
    {
        get
        {
            lock (ProcessLeaseGate) return nativeReleaseTerminalFailure is not null;
        }
    }

    public static void ConfigureNativeRuntimeDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        lock (ProcessLeaseGate)
        {
            if (nativeLibrariesReleasing != 0)
                throw new InvalidOperationException("Resonance native runtime is still releasing; retry after teardown completes");
            if (nativeReleaseTerminalFailure is not null)
                throw new InvalidOperationException(
                    "Resonance native runtime has a failed library release; restart is required before reconfiguring",
                    nativeReleaseTerminalFailure);
            if (nativeRuntimeDirectory is not null
                && !String.Equals(nativeRuntimeDirectory, fullPath, StringComparison.OrdinalIgnoreCase)
                && (processLeaseUsers != 0 || ggmlBaseHandle != 0 || ggmlHandle != 0 || qwenHandle != 0))
                throw new InvalidOperationException("Resonance native runtime cannot change directory while contexts or native handles are active");

            var previousDirectory = nativeRuntimeDirectory;
            var previousGgmlBaseHandle = ggmlBaseHandle;
            var previousGgmlHandle = ggmlHandle;
            nativeRuntimeDirectory = fullPath;
            nativeLibrariesReleased = 0;
            try
            {
                if (ggmlBaseHandle == 0)
                    ggmlBaseHandle = NativeLibrary.Load(Path.Combine(nativeRuntimeDirectory, "ggml-base.dll"));
                if (ggmlHandle == 0)
                    ggmlHandle = NativeLibrary.Load(Path.Combine(nativeRuntimeDirectory, "ggml.dll"));
                if (Interlocked.Exchange(ref resolverInstalled, 1) == 0)
                    NativeLibrary.SetDllImportResolver(typeof(QwenNative).Assembly, ResolveNativeLibrary);
            }
            catch (Exception error)
            {
                var cleanupFailures = new List<Exception>();
                var ggmlCleanupBlocked = false;
                if (ggmlHandle != previousGgmlHandle)
                {
                    TryFreeHandle(ref ggmlHandle, "ggml", cleanupFailures);
                    ggmlCleanupBlocked = ggmlHandle != previousGgmlHandle;
                }
                if (!ggmlCleanupBlocked && ggmlBaseHandle != previousGgmlBaseHandle)
                    TryFreeHandle(ref ggmlBaseHandle, "ggml-base", cleanupFailures);
                if (ggmlHandle == 0 && ggmlBaseHandle == 0)
                {
                    nativeRuntimeDirectory = previousDirectory;
                    nativeLibrariesReleased = previousDirectory is null ? 1 : 0;
                    nativeReleaseTerminalFailure = null;
                }
                else
                {
                    nativeRuntimeDirectory = fullPath;
                    nativeLibrariesReleased = 0;
                    nativeReleaseTerminalFailure = new AggregateException(
                        "Native runtime rollback could not free every library handle", cleanupFailures);
                }
                if (cleanupFailures.Count == 0) throw;
                throw new AggregateException("Native runtime configuration and rollback failed",
                    new[] { error }.Concat(cleanupFailures));
            }
        }
    }

    private static nint ResolveNativeLibrary(string libraryName, System.Reflection.Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("qwen", StringComparison.OrdinalIgnoreCase)
            && !libraryName.Equals("qwen.dll", StringComparison.OrdinalIgnoreCase)) return 0;
        var directory = nativeRuntimeDirectory
            ?? throw new DllNotFoundException("Resonance native runtime directory has not been configured");
        var path = Path.Combine(directory, "qwen.dll");
        lock (ProcessLeaseGate)
        {
            if (nativeLibrariesReleasing != 0 || nativeLibrariesReleased != 0 || nativeReleaseTerminalFailure is not null)
                throw new ObjectDisposedException(nameof(QwenCppRuntime), "Native runtime has not been configured for this generation");
            if (qwenHandle == 0) qwenHandle = NativeLibrary.Load(path);
            return qwenHandle;
        }
    }

    /// <summary>
    /// Releases the process-owned native modules only after every runtime and
    /// backend enumeration lease has ended.  Bootstrap calls this at plugin
    /// shutdown.  The released state closes the current native generation; a
    /// later ConfigureNativeRuntimeDirectory call may create a fresh generation
    /// after every context and process lease has ended.
    /// </summary>
    public static void ReleaseNativeLibraries()
    {
        lock (ProcessLeaseGate)
        lock (BackendPathGate)
        {
            if (nativeReleaseTerminalFailure is not null)
                throw new InvalidOperationException(
                    "Resonance native library cleanup failed; restart is required",
                    nativeReleaseTerminalFailure);
            if (processLeaseUsers != 0 || processOwnerUsers != 0
                || nativeLibrariesReleased != 0 || nativeLibrariesReleasing != 0) return;
            nativeLibrariesReleasing = 1;
            try
            {
                var failures = new List<Exception>();
                if (qwenHandle != 0)
                {
                    TryFreeHandle(ref qwenHandle, "qwen", failures);
                    if (qwenHandle != 0)
                    {
                        var failure = new AggregateException(
                            "Native qwen library release failed; dependent modules were retained and restart is required",
                            failures);
                        nativeLibrariesReleased = 0;
                        nativeReleaseTerminalFailure = failure;
                        throw failure;
                    }
                }
                if (ggmlHandle != 0)
                {
                    TryFreeHandle(ref ggmlHandle, "ggml", failures);
                    if (ggmlHandle != 0)
                    {
                        var failure = new AggregateException(
                            "Native ggml library release failed; dependent ggml-base module was retained and restart is required",
                            failures);
                        nativeLibrariesReleased = 0;
                        nativeReleaseTerminalFailure = failure;
                        throw failure;
                    }
                }
                TryFreeHandle(ref ggmlBaseHandle, "ggml-base", failures);
                if (failures.Count > 0)
                {
                    nativeLibrariesReleased = 0;
                    var failure = new AggregateException(
                        "Native runtime library release failed; handles were retained and restart is required", failures);
                    nativeReleaseTerminalFailure = failure;
                    throw failure;
                }
                nativeReleaseTerminalFailure = null;
                nativeRuntimeDirectory = null;
                ConfiguredBackendPaths.Clear();
                nativeLibrariesReleased = 1;
            }
            finally { nativeLibrariesReleasing = 0; }
        }
    }

    private static void TryFreeHandle(ref nint handle, string name, List<Exception> failures)
    {
        var current = handle;
        if (current == 0) return;
        try
        {
            NativeLibrary.Free(current);
            handle = 0;
        }
        catch (Exception error)
        {
            failures.Add(new InvalidOperationException(
                $"Native runtime library '{name}' could not be freed", error));
        }
    }

    internal unsafe QwenCppRuntime(string talkerPath, string codecPath, string? backendName,
        bool ownsProcessLease = true, IProcessLifetimeLease? pluginLifetimeLease = null)
    {
        if (!ownsProcessLease && pluginLifetimeLease is null)
            throw new InvalidOperationException(
                "A non-owning native runtime requires the plugin process lifetime lease");
        if (pluginLifetimeLease?.IsPoisoned == true)
            throw new InvalidOperationException(
                "The plugin process lifetime lease is poisoned; restart is required");
        this.ownsProcessLease = ownsProcessLease;
        this.pluginLifetimeLease = pluginLifetimeLease;
        AcquireProcessLease(ownsProcessLease);
        try
        {
            ValidateAbi();
            Capabilities = new(true, true, false, false,
                EnumerateBackends(ownsProcessLease: ownsProcessLease,
                    pluginLifetimeLease: pluginLifetimeLease));

            QwenNative.InitDefaultParams(out var parameters);
            using var talker = new Utf8String(talkerPath);
            using var codec = new Utf8String(codecPath);
            using var backend = new Utf8String(backendName);
            unsafe
            {
                parameters.TalkerPath = talker.Pointer;
                parameters.CodecPath = codec.Pointer;
                parameters.BackendName = backend.Pointer;
            }
            parameters.MaxBatch = 1;
            context = QwenNative.Init(ref parameters);
            if (context == 0) throw NativeFailure("qt_init", -1);
        }
        catch
        {
            lifetimeCancellation.Dispose();
            ReleaseProcessLease();
            throw;
        }
    }

    internal static unsafe IReadOnlyList<BackendInfo> EnumerateBackends(
        string? additionalSearchPath = null, bool ownsProcessLease = true,
        IProcessLifetimeLease? pluginLifetimeLease = null)
    {
        if (!ownsProcessLease && pluginLifetimeLease is null)
            throw new InvalidOperationException(
                "A non-owning backend enumeration requires the plugin process lifetime lease");
        if (pluginLifetimeLease?.IsPoisoned == true)
            throw new InvalidOperationException(
                "The plugin process lifetime lease is poisoned; restart is required");
        AcquireProcessLease(ownsProcessLease);
        try
        {
            ValidateAbi();
            ConfigureBackendSearchPath(nativeRuntimeDirectory
                ?? throw new InvalidOperationException("Resonance native runtime directory has not been configured"));
            if (additionalSearchPath is not null) ConfigureBackendSearchPath(additionalSearchPath);
            var result = new List<BackendInfo>();
            var count = QwenNative.BackendCount();
            for (var index = 0; index < count; index++)
            {
                if (QwenNative.BackendGetInfo(index, out var native) != 0) continue;
                result.Add(new(
                    Marshal.PtrToStringUTF8((nint)native.Name) ?? $"backend-{index}",
                    Marshal.PtrToStringUTF8((nint)native.Description) ?? string.Empty,
                    native.Type switch
                    {
                        1 => BackendType.Cpu,
                        2 => BackendType.Cuda,
                        3 => BackendType.Vulkan,
                        4 => BackendType.Gpu,
                        5 => BackendType.Accelerator,
                        _ => BackendType.Unknown,
                    },
                    native.DeviceIndex,
                    native.MemoryFree,
                    native.MemoryTotal));
            }
            return result;
        }
        finally { ReleaseProcessLeaseCore(ownsProcessLease); }
    }

    private static void ConfigureBackendSearchPath(string directory)
    {
        lock (ProcessLeaseGate)
        lock (BackendPathGate)
        {
            var canonical = Path.GetFullPath(directory);
            if (ConfiguredBackendPaths.Contains(canonical)) return;
            Directory.CreateDirectory(canonical);
            var status = QwenNative.BackendLoadFromPath(canonical);
            if (status != 0) throw NativeFailure("qt_backend_load_from_path", status);
            ConfiguredBackendPaths.Add(canonical);
        }
    }

    public ValueTask<VoiceReference> ExtractReferenceAsync(
        ReadOnlyMemory<float> monoPcm24Khz,
        string transcript,
        CancellationToken token)
        => ValueTask.FromException<VoiceReference>(
            new NotSupportedException("Base reference extraction is owned by the external helper process"));

    public Task SynthesizeAsync(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token) =>
        RunSynthesisAsync(request, sink, token, true);

    internal Task SynthesizeAttemptAsync(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token) =>
        RunSynthesisAsync(request, sink, token, false);

    private async Task RunSynthesisAsync(SynthesisRequest request, StreamingAudioBuffer sink,
        CancellationToken token, bool completeOnFailure)
    {
        EnterOperation();
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                token, lifetimeCancellation.Token);
            await Task.Run(() => Synthesize(request, sink, lifetime.Token, completeOnFailure),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally { ExitOperation(); }
    }

    private unsafe void Synthesize(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token, bool completeOnFailure)
    {
        token.ThrowIfCancellationRequested();
        QwenNative.TtsDefaultParams(out var parameters);
        using var text = new Utf8String(request.Text);
        using var language = new Utf8String(request.Language);
        using var instruction = new Utf8String(request.Instruction);
        using var referenceText = new Utf8String(request.Reference?.Transcript);
        parameters.Text = text.Pointer;
        parameters.Language = language.Pointer;
        parameters.Instruction = instruction.Pointer;
        parameters.ReferenceText = referenceText.Pointer;
        parameters.Seed = request.Seed;
        parameters.MaxNewTokens = request.MaxNewTokens;

        var stateHandle = GCHandle.Alloc(new CallbackState(sink, token));
        parameters.Cancel = Marshal.GetFunctionPointerForDelegate(CancelThunk);
        parameters.CancelUserData = (void*)GCHandle.ToIntPtr(stateHandle);
        parameters.OnChunk = Marshal.GetFunctionPointerForDelegate(ChunkThunk);
        parameters.OnChunkUserData = (void*)GCHandle.ToIntPtr(stateHandle);

        QwenNative.Audio audio = default;
        try
        {
            fixed (float* embedding = request.Reference?.SpeakerEmbedding)
            fixed (int* codes = request.Reference?.RvqCodes)
            {
                if (request.Reference is not null)
                {
                    parameters.ReferenceSpeakerEmbedding = embedding;
                    parameters.ReferenceSpeakerDimension = request.Reference.SpeakerEmbedding.Length;
                    parameters.ReferenceCodes = codes;
                    parameters.ReferenceLength = request.Reference.RvqLength;
                }
                var status = QwenNative.Synthesize(context, ref parameters, out audio);
                if (status == -5 || token.IsCancellationRequested) throw new OperationCanceledException(token);
                if (status != 0) throw NativeFailure("qt_synthesize", status);
                sink.Complete();
            }
        }
        catch (Exception error)
        {
            if (completeOnFailure) sink.Complete(error);
            throw;
        }
        finally
        {
            QwenNative.AudioFree(ref audio);
            stateHandle.Free();
        }
    }

    private static unsafe byte OnCancel(void* userData)
    {
        var state = (CallbackState)GCHandle.FromIntPtr((nint)userData).Target!;
        return state.Token.IsCancellationRequested ? (byte)1 : (byte)0;
    }

    private static unsafe byte OnChunk(float* samples, int sampleCount, void* userData)
    {
        var state = (CallbackState)GCHandle.FromIntPtr((nint)userData).Target!;
        if (state.Token.IsCancellationRequested) return 0;
        return state.Sink.TryWrite(new ReadOnlySpan<float>(samples, sampleCount)) ? (byte)1 : (byte)0;
    }

    private static unsafe void ValidateAbi()
    {
        QwenNative.GetAbiInfo(out var info);
        if (info.AbiVersion != QwenNative.AbiVersion
            || info.InitParamsSize != Marshal.SizeOf<QwenNative.InitParams>()
            || info.TtsParamsSize != Marshal.SizeOf<QwenNative.TtsParams>()
            || info.AudioSize != Marshal.SizeOf<QwenNative.Audio>())
        {
            throw new BadImageFormatException(
                $"qwen ABI mismatch: native v{info.AbiVersion}, init={info.InitParamsSize}, tts={info.TtsParamsSize}; " +
                $"managed v{QwenNative.AbiVersion}, init={Marshal.SizeOf<QwenNative.InitParams>()}, tts={Marshal.SizeOf<QwenNative.TtsParams>()}");
        }
    }

    private static unsafe Exception NativeFailure(string operation, int status) =>
        new QwenNativeException(operation, status, Marshal.PtrToStringUTF8((nint)QwenNative.LastError()));

    private void EnterOperation()
    {
        lock (operationGate)
        {
            if (pluginLifetimeLease?.IsPoisoned == true)
                throw new InvalidOperationException(
                    "The plugin process lifetime lease is poisoned; restart is required");
            if (disposing != 0 || context == 0) throw new ObjectDisposedException(nameof(QwenCppRuntime));
            activeOperations++;
        }
    }

    private void ExitOperation()
    {
        lock (operationGate)
        {
            if (--activeOperations != 0) return;
            operationsIdle?.TrySetResult();
            operationsIdle = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task idle;
        Task? existing;
        TaskCompletionSource completion;
        var owner = false;
        lock (operationGate)
        {
            existing = disposalCompletion?.Task;
            if (existing is null)
            {
                disposing = 1;
                completion = disposalCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                idle = activeOperations == 0
                    ? Task.CompletedTask
                    : (operationsIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                owner = true;
            }
            else
            {
                completion = null!;
                idle = Task.CompletedTask;
            }
        }
        if (!owner)
        {
            await existing!.ConfigureAwait(false);
            return;
        }

        var nativeContextReleased = false;
        try
        {
            try { lifetimeCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            await idle.ConfigureAwait(false);
            var value = context;
            if (value != 0)
            {
                // Keep the native context and process lease published until
                // Free returns.  A failed free is terminal: the context must
                // remain rooted and no caller may re-enter it or pretend it
                // can be safely retried in-process.
                QwenNative.Free(value);
                context = 0;
            }
            nativeContextReleased = true;
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref terminalDisposalFailure, 1);
            completion.TrySetException(error);
            throw;
        }
        finally
        {
            // Keep the process lease if native context destruction failed.
            // A leaked lease is recoverable only by process exit, but freeing
            // the module while native state may still exist is not safe.
            if (nativeContextReleased)
            {
                lifetimeCancellation.Dispose();
                ReleaseProcessLease();
            }
        }
    }

    private static void AcquireProcessLease(bool ownsProcessLease)
    {
        lock (ProcessLeaseGate)
        {
            if (nativeLibrariesReleasing != 0 || nativeLibrariesReleased != 0 || nativeReleaseTerminalFailure is not null)
                throw new ObjectDisposedException(nameof(QwenCppRuntime), "Native runtime has not been configured for this generation");
            if (ownsProcessLease && processOwnerUsers == 0)
            {
                var semaphore = new Semaphore(1, 1, "Local\\Resonance.QwenNativeOwner");
                if (!semaphore.WaitOne(0))
                {
                    semaphore.Dispose();
                    throw new InvalidOperationException(
                        "Resonance native runtime is already owned by another plugin instance; restart the game after a dev reload");
                }
                processLease = semaphore;
            }
            processLeaseUsers++;
            if (ownsProcessLease) processOwnerUsers++;
        }
    }

    private void ReleaseProcessLease()
    {
        if (Interlocked.Exchange(ref leaseReleased, 1) != 0) return;
        ReleaseProcessLeaseCore(ownsProcessLease);
    }

    private static void ReleaseProcessLeaseCore(bool ownsProcessLease)
    {
        lock (ProcessLeaseGate)
        {
            processLeaseUsers--;
            if (ownsProcessLease && --processOwnerUsers == 0)
            {
                try { processLease?.Release(); }
                catch (SemaphoreFullException) { }
                processLease?.Dispose();
                processLease = null;
            }
        }
    }

    private sealed class Utf8String : IDisposable
    {
        private nint value;
        internal unsafe byte* Pointer => (byte*)value;
        internal Utf8String(string? text) => value = text is null ? 0 : Marshal.StringToCoTaskMemUTF8(text);
        public void Dispose()
        {
            var pointer = Interlocked.Exchange(ref value, 0);
            if (pointer != 0) Marshal.FreeCoTaskMem(pointer);
        }
    }
}
