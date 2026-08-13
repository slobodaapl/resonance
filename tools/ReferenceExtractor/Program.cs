using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Resonance.Bootstrap;
using Resonance.Tts;

namespace Resonance.ReferenceExtractor;

internal static class Program
{
    // The helper is the sole Base-model host. VoiceDesign remains in the
    // Dalamud process and does not own this cross-process Base lease.
    private const string OwnerName = "Local\\Resonance.QwenNativeOwner";
    private const string LaunchPermitName = "launch.ready";
    private static readonly TimeSpan LaunchPermitTimeout = TimeSpan.FromSeconds(30);

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && String.Equals(args[0], "--host", StringComparison.Ordinal))
                return RunHost(args[1]);
            return Run(args);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"reference extraction failed: {error.Message}");
            return (int)ExitCode.Extraction;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length != 2 || !String.Equals(args[0], "--request", StringComparison.Ordinal))
            return Fail(ExitCode.InvalidRequest, "usage: ReferenceExtractor --request <absolute-request-path>");
        if (!Path.IsPathFullyQualified(args[1]) || !File.Exists(args[1]))
            return Fail(ExitCode.InvalidRequest, "request path is missing or not absolute");
        if (new FileInfo(args[1]).Length > ReferenceExtractionProtocol.MaximumRequestBytes)
            return Fail(ExitCode.InvalidRequest, "request is too large");

        ReferenceExtractionRequest request;
        try
        {
            var json = File.ReadAllText(args[1]);
            request = JsonSerializer.Deserialize<ReferenceExtractionRequest>(
                json, ReferenceExtractionProtocol.JsonOptions())
                ?? throw new InvalidDataException("request is empty");
            ReferenceExtractionProtocol.ValidateRequest(request, requireInput: true,
                validateHelperIdentity: true);
            ReferenceExtractionProtocol.ValidateTransientPath(
                args[1], request.TrustedReferenceRoot, "request path");
            PublishOwnership(request);
            if (!WaitForLaunchPermit(request))
                return Fail(ExitCode.InvalidRequest, "parent did not confirm native helper containment");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException
                                      or UnauthorizedAccessException or NotSupportedException)
        {
            return Fail(ExitCode.InvalidRequest, error.Message);
        }

        using var owner = new Semaphore(1, 1, OwnerName);
        if (!owner.WaitOne(0)) return Fail(ExitCode.NativeBusy, "native runtime is owned by another process");
        try { return Extract(request); }
        finally
        {
            try { owner.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    private static int RunHost(string requestPath)
    {
        if (!Path.IsPathFullyQualified(requestPath) || !File.Exists(requestPath))
            return Fail(ExitCode.InvalidRequest, "host request path is missing or not absolute");
        if (new FileInfo(requestPath).Length > BaseHostProtocol.MaximumFrameBytes)
            return Fail(ExitCode.InvalidRequest, "host request is too large");

        BaseHostLaunchRequest request;
        try
        {
            request = JsonSerializer.Deserialize<BaseHostLaunchRequest>(
                File.ReadAllText(requestPath), BaseHostProtocol.JsonOptions())
                ?? throw new InvalidDataException("host request is empty");
            BaseHostProtocol.ValidateLaunchRequest(request, validateHelperIdentity: true);
            ReferenceExtractionProtocol.ValidateTransientPath(
                requestPath, request.TrustedReferenceRoot, "host request path");
            var hostRoot = Path.GetFullPath(request.TrustedHostRoot);
            var ownerPath = Path.Combine(hostRoot, "owner.json");
            var ownerTemporary = ownerPath + ".part";
            var permitPath = Path.Combine(hostRoot, "launch.ready");
            ReferenceExtractionProtocol.ValidateTransientPath(ownerPath, hostRoot, "host ownership metadata");
            ReferenceExtractionProtocol.ValidateTransientPath(ownerTemporary, hostRoot, "host ownership temporary");
            ReferenceExtractionProtocol.ValidateTransientPath(permitPath, hostRoot, "host launch permit");
            using var process = Process.GetCurrentProcess();
            var owner = new ReferenceExtractionOwnership(
                process.Id, process.StartTime.ToUniversalTime().Ticks, request.RequestNonce);
            File.WriteAllText(ownerTemporary, JsonSerializer.Serialize(owner));
            File.Move(ownerTemporary, ownerPath, true);
            if (!WaitForLaunchPermit(permitPath, request.RequestNonce))
                return Fail(ExitCode.InvalidRequest, "parent did not confirm host containment");

            using var nativeOwner = new Semaphore(1, 1, OwnerName);
            if (!nativeOwner.WaitOne(0)) return Fail(ExitCode.NativeBusy, "native runtime is owned by another host");
            try
            {
                using var runtime = new BaseHostRuntime(request);
                using var server = new Resonance.ReferenceExtractor.BaseHostServer(runtime,
                    Console.OpenStandardInput(), Console.OpenStandardOutput());
                server.RunAsync().GetAwaiter().GetResult();
                return (int)ExitCode.Success;
            }
            finally
            {
                try { nativeOwner.Release(); }
                catch (SemaphoreFullException) { }
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException
                                      or UnauthorizedAccessException or NotSupportedException)
        {
            return Fail(ExitCode.InvalidRequest, error.Message);
        }
    }

    private static bool WaitForLaunchPermit(string path, string nonce)
    {
        var deadline = DateTime.UtcNow + LaunchPermitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                    return String.Equals(File.ReadAllText(path), nonce, StringComparison.Ordinal);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Thread.Sleep(TimeSpan.FromMilliseconds(50));
        }
        return false;
    }


    internal sealed class BaseHostRuntime : Resonance.ReferenceExtractor.IBaseHostServerRuntime
    {
        private static readonly unsafe QwenNative.CancelCallback CancelThunk = OnCancel;
        private static readonly unsafe QwenNative.AudioChunkCallback ChunkThunk = OnChunk;
        private readonly BaseHostLaunchRequest launch;
        private readonly object stateGate = new();
        private readonly CancellationTokenSource processCancellation = new();
        private nint ggmlBase;
        private nint ggml;
        private nint qwen;
        private readonly List<(string Name, nint Handle)> backendDependencies = [];
        private nint context;
        private CancellationTokenSource? synthesisCancellation;
        private string? synthesisRequestId;
        private readonly HashSet<string> canceledOperations = new(StringComparer.Ordinal);
        private int extractionActive;
        private int nativeOwnershipPoisoned;
        private int disposed;
        private static int resolverInstalled;

        public string BackendName { get; private set; }
        public bool IsTerminalPoisoned => Volatile.Read(ref nativeOwnershipPoisoned) != 0;
        public bool ContextReady => context != 0 && !IsTerminalPoisoned
                                     && Volatile.Read(ref disposed) == 0;
        public string? ActiveBackendId => ContextReady ? BackendName : null;
        public bool ExtractionActive => Volatile.Read(ref extractionActive) != 0;
        public bool SynthesisActive
        {
            get { lock (stateGate) return synthesisCancellation is not null; }
        }

        internal BaseHostRuntime(BaseHostLaunchRequest launch)
        {
            this.launch = launch;
            BackendName = launch.BackendName;
            InitializeNative();
        }

        private unsafe void InitializeNative()
        {
            ggmlBase = NativeLibrary.Load(TrustedRuntimeLibrary(launch.RuntimeDirectory, "ggml-base.dll"));
            ggml = NativeLibrary.Load(TrustedRuntimeLibrary(launch.RuntimeDirectory, "ggml.dll"));
            qwen = NativeLibrary.Load(TrustedRuntimeLibrary(launch.RuntimeDirectory, "qwen.dll"));
            if (Interlocked.Exchange(ref resolverInstalled, 1) == 0)
                NativeLibrary.SetDllImportResolver(typeof(QwenNative).Assembly, (_, _, _) => qwen);
            ValidateAbi();
            EnsureBackendDependencies(BackendName);
            if (QwenNative.BackendLoadFromPath(launch.RuntimeDirectory) != 0)
                throw new InvalidDataException("Base runtime host backend loading failed");
            InitializeContext(BackendName);
        }

        private unsafe void InitializeContext(string backend)
        {
            QwenNative.InitDefaultParams(out var parameters);
            using var talker = new Utf8(launch.TalkerPath);
            using var codec = new Utf8(launch.CodecPath);
            using var backendName = new Utf8(backend);
            parameters.TalkerPath = talker.Pointer;
            parameters.CodecPath = codec.Pointer;
            parameters.BackendName = backendName.Pointer;
            parameters.MaxBatch = 1;
            context = QwenNative.Init(ref parameters);
            if (context == 0)
                throw new BackendInitializationException("Base runtime host model initialization failed");
            BackendName = backend;
        }

        private sealed class BackendInitializationException(string message) : InvalidOperationException(message);

        public bool TryBeginExtraction() => Interlocked.CompareExchange(ref extractionActive, 1, 0) == 0;
        public void EndExtraction() => Volatile.Write(ref extractionActive, 0);

        public bool TryBeginSynthesis(string requestId)
        {
            lock (stateGate)
            {
                if (synthesisCancellation is not null || ExtractionActive) return false;
                synthesisRequestId = requestId;
                synthesisCancellation = CancellationTokenSource.CreateLinkedTokenSource(processCancellation.Token);
                return true;
            }
        }

        public void EndSynthesis(string requestId)
        {
            lock (stateGate)
            {
                if (!String.Equals(synthesisRequestId, requestId, StringComparison.Ordinal)) return;
                synthesisCancellation?.Dispose();
                synthesisCancellation = null;
                synthesisRequestId = null;
            }
        }

        public void CancelSynthesis(string requestId)
        {
            lock (stateGate)
            {
                if (synthesisRequestId is null || String.IsNullOrEmpty(requestId)
                    || String.Equals(synthesisRequestId, requestId, StringComparison.Ordinal))
                    synthesisCancellation?.Cancel();
            }
        }

        public void CancelOperation(string requestId, BaseHostFrameKind targetKind)
        {
            lock (stateGate)
            {
                canceledOperations.Add(requestId);
                if ((targetKind is BaseHostFrameKind.Synthesize or BaseHostFrameKind.Benchmark)
                    && (String.IsNullOrEmpty(requestId)
                        || String.Equals(synthesisRequestId, requestId, StringComparison.Ordinal)))
                    synthesisCancellation?.Cancel();
            }
        }

        public bool IsOperationCancellationRequested(string requestId)
        {
            lock (stateGate)
                return canceledOperations.Contains(requestId)
                    || (String.Equals(synthesisRequestId, requestId, StringComparison.Ordinal)
                        && synthesisCancellation?.IsCancellationRequested == true);
        }

        public void ClearOperationCancellation(string requestId)
        {
            lock (stateGate) canceledOperations.Remove(requestId);
        }

        internal void CancelActive()
        {
            try { processCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            CancelSynthesis(String.Empty);
        }

        public void Shutdown() => CancelActive();

        public BaseHostReferencePayload Extract(BaseHostExtractPayload payload)
        {
            if (Volatile.Read(ref nativeOwnershipPoisoned) != 0)
                throw new InvalidOperationException(
                    "Base runtime host native ownership is poisoned; restart is required");
            if (context == 0)
                throw new InvalidOperationException(
                    "Base runtime host backend is not initialized; select another backend");
            ArgumentNullException.ThrowIfNull(payload);
            var transcript = payload.Transcript
                ?? throw new InvalidDataException("host transcript is missing");
            var inputPcmPath = payload.InputPcmPath
                ?? throw new InvalidDataException("host input PCM path is missing");
            if (String.IsNullOrWhiteSpace(transcript)
                || transcript.Length > ReferenceExtractionProtocol.MaximumTranscriptCharacters)
                throw new InvalidDataException("host transcript is invalid");
            ReferenceExtractionProtocol.ValidateTransientPath(
                inputPcmPath, launch.TrustedHostRoot, "host input PCM");
            var bytes = File.ReadAllBytes(inputPcmPath);
            if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0
                || bytes.Length > ReferenceExtractionProtocol.MaximumSamples * sizeof(float))
                throw new InvalidDataException("host PCM input size is invalid");
            var samples = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            if (samples.Any(value => !float.IsFinite(value)))
                throw new InvalidDataException("host PCM input contains non-finite samples");
            unsafe
            {
                fixed (float* input = samples)
                {
                    var status = QwenNative.ExtractVoiceRef(context, input, samples.Length, out var reference);
                    if (status != 0) throw new InvalidOperationException($"host reference extraction failed ({status})");
                    try
                    {
                        var nativeEmbedding = reference.SpeakerEmbedding;
                        var nativeCodes = reference.Codes;
                        if (nativeEmbedding == null || nativeCodes == null
                            || reference.SpeakerDimension <= 0 || reference.ReferenceLength <= 0
                            || reference.Codebooks <= 0)
                            throw new InvalidDataException("host reference metadata is invalid");
                        BaseHostProtocol.ValidateVoiceReferenceShape(reference.SpeakerDimension,
                            reference.ReferenceLength, reference.Codebooks);
                        var embedding = new float[reference.SpeakerDimension];
                        var codes = new int[checked(reference.ReferenceLength * reference.Codebooks)];
                        Marshal.Copy((nint)nativeEmbedding, embedding, 0, embedding.Length);
                        Marshal.Copy((nint)nativeCodes, codes, 0, codes.Length);
                        var result = new BaseHostReferencePayload(embedding, codes,
                            reference.ReferenceLength, reference.Codebooks, transcript);
                        BaseHostProtocol.ValidateVoiceReferencePayload(result, transcript,
                            reference.SpeakerDimension);
                        return result;
                    }
                    finally { QwenNative.VoiceRefFree(ref reference); }
                }
            }
        }

        public unsafe void Synthesize(BaseHostSynthesisPayload payload, string requestId,
            Resonance.ReferenceExtractor.AudioSender sendAudio)
        {
            if (Volatile.Read(ref nativeOwnershipPoisoned) != 0)
                throw new InvalidOperationException(
                    "Base runtime host native ownership is poisoned; restart is required");
            if (context == 0)
                throw new InvalidOperationException(
                    "Base runtime host backend is not initialized; select another backend");
            ArgumentNullException.ThrowIfNull(payload);
            ArgumentNullException.ThrowIfNull(sendAudio);
            var textValue = payload.Text
                ?? throw new InvalidDataException("host synthesis text is missing");
            var languageValue = payload.Language
                ?? throw new InvalidDataException("host synthesis language is missing");
            var reference = payload.Reference;
            if (String.IsNullOrWhiteSpace(textValue) || textValue.Length > 20_000
                || String.IsNullOrWhiteSpace(languageValue) || languageValue.Length > 64
                || payload.MaxNewTokens <= 0 || payload.MaxNewTokens > 32_768)
                throw new InvalidDataException("host synthesis request is invalid");
            if (reference is not null)
                BaseHostProtocol.ValidateVoiceReferencePayload(reference);
            CancellationToken token;
            lock (stateGate) token = synthesisCancellation?.Token
                ?? throw new InvalidOperationException("Base synthesis is not active");
            token.ThrowIfCancellationRequested();
            QwenNative.TtsDefaultParams(out var parameters);
            using var text = new Utf8(textValue);
            using var language = new Utf8(languageValue);
            using var instruction = new Utf8(payload.Instruction);
            using var referenceText = new Utf8(reference?.Transcript);
            parameters.Text = text.Pointer;
            parameters.Language = language.Pointer;
            parameters.Instruction = instruction.Pointer;
            parameters.ReferenceText = referenceText.Pointer;
            parameters.Seed = payload.Seed;
            parameters.MaxNewTokens = payload.MaxNewTokens;
            var state = GCHandle.Alloc(new HostChunkState(sendAudio, requestId, token));
            parameters.Cancel = Marshal.GetFunctionPointerForDelegate(CancelThunk);
            parameters.CancelUserData = (void*)GCHandle.ToIntPtr(state);
            parameters.OnChunk = Marshal.GetFunctionPointerForDelegate(ChunkThunk);
            parameters.OnChunkUserData = (void*)GCHandle.ToIntPtr(state);
            QwenNative.Audio audio = default;
            try
            {
                var embeddingValues = reference?.SpeakerEmbedding ?? Array.Empty<float>();
                var codeValues = reference?.RvqCodes ?? Array.Empty<int>();
                fixed (float* embedding = embeddingValues)
                fixed (int* codes = codeValues)
                {
                    if (reference is not null)
                    {
                        parameters.ReferenceSpeakerEmbedding = embedding;
                        parameters.ReferenceSpeakerDimension = reference.SpeakerEmbedding.Length;
                        parameters.ReferenceCodes = codes;
                        parameters.ReferenceLength = reference.RvqLength;
                    }
                    var status = QwenNative.Synthesize(context, ref parameters, out audio);
                    if (status == -5 || token.IsCancellationRequested)
                        throw new OperationCanceledException(token);
                    if (status != 0) throw new InvalidOperationException($"host synthesis failed ({status})");
                }
            }
            finally
            {
                try { QwenNative.AudioFree(ref audio); }
                finally { state.Free(); }
            }
        }

        public void SwitchBackend(string backend, string requestId)
        {
            lock (stateGate)
            {
                if (canceledOperations.Contains(requestId))
                    throw new OperationCanceledException("Base backend switch canceled.");
                if (synthesisCancellation is not null || ExtractionActive)
                    throw new InvalidOperationException("Base runtime host is busy");
                if (Volatile.Read(ref nativeOwnershipPoisoned) != 0)
                    throw new InvalidOperationException(
                        "Base runtime host native ownership is poisoned; restart is required");
                EnsureBackendDependencies(backend);
                if (QwenNative.BackendLoadFromPath(launch.RuntimeDirectory) != 0)
                    throw new InvalidDataException("Base runtime host backend loading failed");
                var previousBackend = BackendName;
                try
                {
                    if (context != 0)
                    {
                        QwenNative.Free(context);
                        context = 0;
                    }
                }
                catch (Exception error)
                {
                    Volatile.Write(ref nativeOwnershipPoisoned, 1);
                    throw new InvalidOperationException(
                        "Base runtime host could not release the previous backend context", error);
                }
                try
                {
                    InitializeContext(backend);
                }
                catch (BackendInitializationException)
                {
                    BackendName = previousBackend;
                    context = 0;
                    throw;
                }
                catch (Exception error)
                {
                    Volatile.Write(ref nativeOwnershipPoisoned, 1);
                    throw new InvalidOperationException(
                        "Base runtime host backend initialization lost native ownership", error);
                }
                if (canceledOperations.Contains(requestId))
                {
                    BackendName = previousBackend;
                    try
                    {
                        if (context != 0)
                        {
                            QwenNative.Free(context);
                            context = 0;
                        }
                    }
                    catch (Exception error)
                    {
                        Volatile.Write(ref nativeOwnershipPoisoned, 1);
                        throw new InvalidOperationException(
                            "Base runtime host could not discard the canceled backend context", error);
                    }
                    throw new OperationCanceledException("Base backend switch canceled.");
                }
            }
        }

        public void SwitchBackend(string backend) => SwitchBackend(backend, String.Empty);

        public IReadOnlyList<BaseHostBenchmarkResult> Benchmark(
            IReadOnlyList<string> backends, string requestId)
        {
            ArgumentNullException.ThrowIfNull(backends);
            var results = new List<BaseHostBenchmarkResult>();
            foreach (var backend in backends)
            {
                if (IsOperationCancellationRequested(requestId))
                    throw new OperationCanceledException("Base benchmark canceled.");
                if (String.IsNullOrWhiteSpace(backend) || backend.Length > 256
                    || backend.Any(char.IsControl))
                    throw new InvalidDataException("host benchmark backend name is invalid");
                var initialization = Stopwatch.StartNew();
                try
                {
                    SwitchBackend(backend, requestId);
                    initialization.Stop();
                    var started = Stopwatch.StartNew();
                    var sampleCount = 0;
                    double? first = null;
                    if (!TryBeginSynthesis(requestId)) throw new InvalidOperationException("host benchmark is busy");
                    try
                    {
                        Synthesize(new("A quiet lantern glows beside the harbor.", "english", null,
                            "A calm, clear adult narrator voice with natural conversational pacing.",
                            0x5245534f4e414e43, 192), requestId, (id, samples) =>
                        {
                            first ??= started.Elapsed.TotalSeconds;
                            sampleCount += samples.Length;
                            return true;
                        });
                    }
                    finally { EndSynthesis(requestId); }
                    if (IsOperationCancellationRequested(requestId))
                        throw new OperationCanceledException("Base benchmark canceled.");
                    started.Stop();
                    var seconds = sampleCount / 24000d;
                    if (seconds < 0.2 || first is null)
                        throw new InvalidDataException("host benchmark produced no audio");
                    results.Add(new(backend, true, initialization.Elapsed.TotalSeconds, first.Value,
                        started.Elapsed.TotalSeconds / seconds, null));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    results.Add(new(backend, false, initialization.Elapsed.TotalSeconds,
                        null, null, error.Message));
                    if (Volatile.Read(ref nativeOwnershipPoisoned) != 0)
                        throw new InvalidOperationException(
                            "Base runtime host native ownership was lost during backend benchmark", error);
                }
            }
            return results;
        }

        private static unsafe byte OnCancel(void* userData)
        {
            try
            {
                if (userData == null) return 1;
                if (GCHandle.FromIntPtr((nint)userData).Target is not HostChunkState state)
                    return 1;
                return state.Token.IsCancellationRequested ? (byte)1 : (byte)0;
            }
            catch { return 1; }
        }

        private static unsafe byte OnChunk(float* samples, int sampleCount, void* userData)
        {
            try
            {
                if (userData == null || samples == null || sampleCount <= 0)
                    return 0;
                if (GCHandle.FromIntPtr((nint)userData).Target is not HostChunkState state)
                    return 0;
                if (state.Token.IsCancellationRequested) return 0;
                return state.SendAudio(state.RequestId,
                    new ReadOnlySpan<float>(samples, sampleCount)) ? (byte)1 : (byte)0;
            }
            catch { return 0; }
        }

        private sealed record HostChunkState(
            Resonance.ReferenceExtractor.AudioSender SendAudio,
            string RequestId, CancellationToken Token);

        private unsafe void ValidateAbi()
        {
            QwenNative.GetAbiInfo(out var info);
            if (info.AbiVersion != QwenNative.AbiVersion
                || info.InitParamsSize != Marshal.SizeOf<QwenNative.InitParams>()
                || info.TtsParamsSize != Marshal.SizeOf<QwenNative.TtsParams>()
                || info.AudioSize != Marshal.SizeOf<QwenNative.Audio>()
                || info.VoiceRefSize != Marshal.SizeOf<QwenNative.VoiceRef>())
                throw new BadImageFormatException("Base runtime host native ABI mismatch");
        }

        private static string TrustedRuntimeLibrary(string root, string name)
        {
            var path = Path.GetFullPath(Path.Combine(root, name));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path)
                || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"trusted runtime library is unavailable: {name}");
            return path;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { processCancellation.Cancel(); }
            catch { }
            lock (stateGate)
            {
                try { synthesisCancellation?.Cancel(); }
                catch { }
                synthesisCancellation?.Dispose();
                synthesisCancellation = null;
                if (context != 0)
                {
                    try
                    {
                        QwenNative.Free(context);
                        context = 0;
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine($"host context cleanup failed: {error.Message}");
                        Volatile.Write(ref nativeOwnershipPoisoned, 1);
                        processCancellation.Dispose();
                        return;
                    }
                }
            }
            if (!TryFree(ref qwen, "qwen"))
            {
                Volatile.Write(ref nativeOwnershipPoisoned, 1);
                processCancellation.Dispose();
                return;
            }
            if (!TryFree(ref ggml, "ggml"))
            {
                Volatile.Write(ref nativeOwnershipPoisoned, 1);
                processCancellation.Dispose();
                return;
            }
            if (!TryFree(ref ggmlBase, "ggml-base"))
                Volatile.Write(ref nativeOwnershipPoisoned, 1);
            if (ggmlBase == 0)
            {
                for (var index = backendDependencies.Count - 1; index >= 0; index--)
                {
                    var dependency = backendDependencies[index];
                    try
                    {
                        NativeLibrary.Free(dependency.Handle);
                        backendDependencies.RemoveAt(index);
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine($"host backend dependency cleanup failed ({dependency.Name}): {error.Message}");
                        Volatile.Write(ref nativeOwnershipPoisoned, 1);
                    }
                }
            }
            processCancellation.Dispose();
        }

        private void EnsureBackendDependencies(string backend)
        {
            if (!OperatingSystem.IsWindows()
                || !backend.StartsWith("CUDA", StringComparison.OrdinalIgnoreCase)
                || backendDependencies.Count != 0) return;
            backendDependencies.Add(("nvcuda.dll", WindowsNativeLibrary.LoadCudaDriver()));
            foreach (var name in new[] { "cudart64_12.dll", "cublasLt64_12.dll", "cublas64_12.dll" })
                backendDependencies.Add((name,
                    NativeLibrary.Load(TrustedRuntimeLibrary(launch.RuntimeDirectory, name))));
        }

        private static bool TryFree(ref nint handle, string name)
        {
            var current = Volatile.Read(ref handle);
            if (current == 0) return true;
            try
            {
                NativeLibrary.Free(current);
                Interlocked.CompareExchange(ref handle, 0, current);
                return Volatile.Read(ref handle) == 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"host {name} cleanup failed: {error.Message}");
                return false;
            }
        }
    }

    private static void PublishOwnership(ReferenceExtractionRequest request)
    {
        var ownerPath = Path.Combine(request.TrustedReferenceRoot, "owner.json");
        var temporary = ownerPath + ".part";
        ReferenceExtractionProtocol.ValidateTransientPath(
            ownerPath, request.TrustedReferenceRoot, "ownership metadata");
        ReferenceExtractionProtocol.ValidateTransientPath(
            temporary, request.TrustedReferenceRoot, "ownership metadata temporary");
        using var process = Process.GetCurrentProcess();
        var ownership = new ReferenceExtractionOwnership(
            process.Id, process.StartTime.ToUniversalTime().Ticks, request.RequestNonce);
        var json = JsonSerializer.Serialize(ownership);
        File.WriteAllText(temporary, json);
        File.Move(temporary, ownerPath, true);
    }

    private static bool WaitForLaunchPermit(ReferenceExtractionRequest request)
    {
        var permitPath = Path.Combine(request.TrustedReferenceRoot, LaunchPermitName);
        ReferenceExtractionProtocol.ValidateTransientPath(
            permitPath, request.TrustedReferenceRoot, "launch permit");
        var deadline = DateTime.UtcNow + LaunchPermitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(permitPath))
                {
                    var value = File.ReadAllText(permitPath);
                    if (String.Equals(value, request.RequestNonce, StringComparison.Ordinal)) return true;
                    return false;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Thread.Sleep(TimeSpan.FromMilliseconds(50));
        }
        return false;
    }

    private static unsafe int Extract(ReferenceExtractionRequest request)
    {
        nint ggmlBase = 0;
        nint ggml = 0;
        nint qwen = 0;
        nint context = 0;
        QwenNative.VoiceRef nativeReference = default;
        var nativeReferenceOwned = false;
        var temporaryOutput = request.OutputPath + ".part";
        try
        {
            // The request was validated before the launch handshake, but its
            // model paths may have changed while the parent was assigning the
            // process.  Revalidate trusted files immediately before any
            // native load/init call.
            ReferenceExtractionProtocol.ValidateRequest(request, requireInput: true,
                validateHelperIdentity: true);
            var ggmlBasePath = TrustedRuntimeLibrary(request.RuntimeDirectory, "ggml-base.dll");
            var ggmlPath = TrustedRuntimeLibrary(request.RuntimeDirectory, "ggml.dll");
            var qwenPath = TrustedRuntimeLibrary(request.RuntimeDirectory, "qwen.dll");
            ggmlBase = NativeLibrary.Load(ggmlBasePath);
            ggml = NativeLibrary.Load(ggmlPath);
            qwen = NativeLibrary.Load(qwenPath);
            NativeLibrary.SetDllImportResolver(typeof(QwenNative).Assembly,
                (_, _, _) => qwen);

            QwenNative.GetAbiInfo(out var abi);
            if (abi.AbiVersion != QwenNative.AbiVersion
                || abi.InitParamsSize != Marshal.SizeOf<QwenNative.InitParams>()
                || abi.TtsParamsSize != Marshal.SizeOf<QwenNative.TtsParams>()
                || abi.AudioSize != Marshal.SizeOf<QwenNative.Audio>()
                || abi.VoiceRefSize != Marshal.SizeOf<QwenNative.VoiceRef>())
                return Fail(ExitCode.NativeAbi, "native ABI mismatch");
            if (QwenNative.BackendLoadFromPath(request.RuntimeDirectory) != 0)
                return Fail(ExitCode.NativeLoad, "backend loading failed");

            QwenNative.InitDefaultParams(out var parameters);
            using var talker = new Utf8(request.TalkerPath);
            using var codec = new Utf8(request.CodecPath);
            using var backend = new Utf8(request.BackendName);
            parameters.TalkerPath = talker.Pointer;
            parameters.CodecPath = codec.Pointer;
            parameters.BackendName = backend.Pointer;
            parameters.MaxBatch = 1;
            context = QwenNative.Init(ref parameters);
            if (context == 0) return Fail(ExitCode.NativeLoad, "base model initialization failed");

            ReferenceExtractionProtocol.ValidateRequest(request, requireInput: true,
                validateHelperIdentity: true);
            var bytes = File.ReadAllBytes(request.InputPcmPath);
            if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0
                || bytes.Length > ReferenceExtractionProtocol.MaximumSamples * sizeof(float))
                return Fail(ExitCode.InvalidInput, "PCM input size is invalid");
            var samples = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            if (samples.Any(value => !float.IsFinite(value)))
                return Fail(ExitCode.InvalidInput, "PCM input contains non-finite samples");

            fixed (float* input = samples)
            {
                var status = QwenNative.ExtractVoiceRef(context, input, samples.Length, out nativeReference);
                if (status != 0) return Fail(ExitCode.Extraction, $"voice reference extraction failed ({status})");
                nativeReferenceOwned = true;
                var nativeEmbedding = nativeReference.SpeakerEmbedding;
                var nativeCodes = nativeReference.Codes;
                if (nativeReference.SpeakerDimension <= 0
                    || nativeReference.SpeakerDimension > BaseHostProtocol.MaximumSpeakerEmbeddingValues
                    || nativeReference.ReferenceLength <= 0
                    || nativeReference.ReferenceLength > ReferenceExtractionProtocol.MaximumSamples
                    || nativeReference.Codebooks <= 0
                    || nativeReference.Codebooks > BaseHostProtocol.MaximumCodebooks
                    || nativeEmbedding == null || nativeCodes == null)
                    return Fail(ExitCode.Extraction, "native voice reference metadata is invalid");
                try
                {
                    BaseHostProtocol.ValidateVoiceReferenceShape(nativeReference.SpeakerDimension,
                        nativeReference.ReferenceLength, nativeReference.Codebooks);
                }
                catch (InvalidDataException error)
                {
                    return Fail(ExitCode.Extraction, error.Message);
                }
                var embedding = new float[nativeReference.SpeakerDimension];
                var codes = new int[checked(nativeReference.ReferenceLength * nativeReference.Codebooks)];
                Marshal.Copy((nint)nativeEmbedding, embedding, 0, embedding.Length);
                Marshal.Copy((nint)nativeCodes, codes, 0, codes.Length);
                var response = new ReferenceExtractionResponse(
                    ReferenceExtractionProtocol.SchemaVersion,
                    ReferenceExtractionProtocol.AbiVersion,
                    nativeReference.SpeakerDimension,
                    nativeReference.ReferenceLength,
                    nativeReference.Codebooks,
                    request.Transcript,
                    embedding,
                    codes);
                var responseJson = JsonSerializer.Serialize(response, ReferenceExtractionProtocol.JsonOptions());
                try
                {
                    ReferenceExtractionProtocol.ParseResponse(responseJson, request.Transcript);
                }
                catch (InvalidDataException error)
                {
                    return Fail(ExitCode.Output, error.Message);
                }
                var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                if (responseBytes.Length > ReferenceExtractionProtocol.MaximumResponseBytes)
                    return Fail(ExitCode.Output, "reference response is too large");
                var outputDirectory = Path.GetDirectoryName(request.OutputPath)
                    ?? throw new InvalidDataException("reference output directory is missing");
                Directory.CreateDirectory(outputDirectory);
                ReferenceExtractionProtocol.ValidateTransientPath(
                    temporaryOutput, request.TrustedReferenceRoot, "output temporary path");
                ReferenceExtractionProtocol.ValidateRequest(request, requireInput: true,
                    validateHelperIdentity: true);
                File.WriteAllBytes(temporaryOutput, responseBytes);
                File.Move(temporaryOutput, request.OutputPath, true);
            }
            return (int)ExitCode.Success;
        }
        catch (InvalidDataException error) { return Fail(ExitCode.InvalidInput, error.Message); }
        catch (FileNotFoundException error) { return Fail(ExitCode.InvalidInput, error.Message); }
        catch (UnauthorizedAccessException error) { return Fail(ExitCode.Output, error.Message); }
        catch (IOException error) { return Fail(ExitCode.Output, error.Message); }
        catch (DllNotFoundException error) { return Fail(ExitCode.NativeLoad, error.Message); }
        catch (BadImageFormatException error) { return Fail(ExitCode.NativeLoad, error.Message); }
        catch (EntryPointNotFoundException error) { return Fail(ExitCode.NativeAbi, error.Message); }
        catch (Exception error)
        {
            return Fail(ExitCode.Extraction, error.Message);
        }
        finally
        {
            try
            {
                CleanupNative(ref nativeReference, nativeReferenceOwned, context, qwen, ggml, ggmlBase);
            }
            finally
            {
                // The temporary response is never allowed to survive a
                // failed/canceled extraction, even when native cleanup has a
                // bad handle. Cleanup diagnostics must not replace the
                // primary extraction result.
                try { File.Delete(temporaryOutput); }
                catch (IOException error) { Console.Error.WriteLine($"temporary cleanup failed: {error.Message}"); }
                catch (UnauthorizedAccessException error) { Console.Error.WriteLine($"temporary cleanup failed: {error.Message}"); }
            }
        }
    }

    private static unsafe void CleanupNative(
        ref QwenNative.VoiceRef nativeReference, bool nativeReferenceOwned,
        nint context, nint qwen, nint ggml, nint ggmlBase)
    {
        if (nativeReferenceOwned)
        {
            try { QwenNative.VoiceRefFree(ref nativeReference); }
            catch (Exception error)
            {
                Console.Error.WriteLine($"voice reference cleanup failed: {error.Message}");
                return;
            }
        }
        if (context != 0)
        {
            try { QwenNative.Free(context); }
            catch (Exception error)
            {
                Console.Error.WriteLine($"native context cleanup failed: {error.Message}");
                return;
            }
        }
        if (qwen != 0)
        {
            try { NativeLibrary.Free(qwen); }
            catch (Exception error)
            {
                Console.Error.WriteLine($"qwen library cleanup failed: {error.Message}");
                return;
            }
        }
        if (ggml != 0)
        {
            try { NativeLibrary.Free(ggml); }
            catch (Exception error)
            {
                Console.Error.WriteLine($"ggml library cleanup failed: {error.Message}");
                return;
            }
        }
        if (ggmlBase != 0)
            try { NativeLibrary.Free(ggmlBase); }
            catch (Exception error) { Console.Error.WriteLine($"ggml-base library cleanup failed: {error.Message}"); }
    }

    private static int Fail(ExitCode code, string message)
    {
        Console.Error.WriteLine(message);
        return (int)code;
    }

    private static string TrustedRuntimeLibrary(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(prefix, comparison) || !File.Exists(path)
            || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"trusted runtime library is unavailable: {name}");
        return path;
    }

    private enum ExitCode
    {
        Success = 0,
        InvalidRequest = 2,
        InvalidInput = 3,
        NativeLoad = 4,
        Extraction = 5,
        Output = 6,
        NativeBusy = 7,
        NativeAbi = 8,
    }

    private sealed class Utf8 : IDisposable
    {
        private nint value;
        internal unsafe byte* Pointer => (byte*)value;
        internal Utf8(string? text) => value = text is null
            ? 0
            : Marshal.StringToCoTaskMemUTF8(text);
        public void Dispose()
        {
            var pointer = Interlocked.Exchange(ref value, 0);
            if (pointer != 0) Marshal.FreeCoTaskMem(pointer);
        }
    }
}
