using System.Runtime.InteropServices;
using Resonance.Audio;

namespace Resonance.Tts;

internal sealed class QwenNativeException(string operation, int status, string? detail)
    : InvalidOperationException($"{operation} failed ({status}): {detail}")
{
    public string Operation { get; } = operation;
    public int Status { get; } = status;
}

public sealed unsafe class QwenCppRuntime : ITtsRuntime
{
    private sealed record CallbackState(StreamingAudioBuffer Sink, CancellationToken Token);

    private static readonly QwenNative.CancelCallback CancelThunk = OnCancel;
    private static readonly QwenNative.AudioChunkCallback ChunkThunk = OnChunk;
    private nint context;
    private static readonly object BackendPathGate = new();
    private static readonly HashSet<string> ConfiguredBackendPaths = new(StringComparer.OrdinalIgnoreCase);
    private static string? nativeRuntimeDirectory;
    private static int resolverInstalled;
    private static nint ggmlBaseHandle;
    private static nint ggmlHandle;

    public RuntimeCapabilities Capabilities { get; }

    public static void ConfigureNativeRuntimeDirectory(string directory)
    {
        nativeRuntimeDirectory = Path.GetFullPath(directory);
        if (ggmlBaseHandle == 0)
            ggmlBaseHandle = NativeLibrary.Load(Path.Combine(nativeRuntimeDirectory, "ggml-base.dll"));
        if (ggmlHandle == 0)
            ggmlHandle = NativeLibrary.Load(Path.Combine(nativeRuntimeDirectory, "ggml.dll"));
        if (Interlocked.Exchange(ref resolverInstalled, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(typeof(QwenNative).Assembly, ResolveNativeLibrary);
    }

    private static nint ResolveNativeLibrary(string libraryName, System.Reflection.Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("qwen", StringComparison.OrdinalIgnoreCase)
            && !libraryName.Equals("qwen.dll", StringComparison.OrdinalIgnoreCase)) return 0;
        var directory = nativeRuntimeDirectory
            ?? throw new DllNotFoundException("Resonance native runtime directory has not been configured");
        var path = Path.Combine(directory, "qwen.dll");
        return NativeLibrary.Load(path);
    }

    public QwenCppRuntime(string talkerPath, string codecPath, string? backendName)
    {
        ValidateAbi();
        Capabilities = new(true, true, false, true, EnumerateBackends());

        QwenNative.InitDefaultParams(out var parameters);
        using var talker = new Utf8String(talkerPath);
        using var codec = new Utf8String(codecPath);
        using var backend = new Utf8String(backendName);
        parameters.TalkerPath = talker.Pointer;
        parameters.CodecPath = codec.Pointer;
        parameters.BackendName = backend.Pointer;
        parameters.MaxBatch = 1;
        context = QwenNative.Init(ref parameters);
        if (context == 0) throw NativeFailure("qt_init", -1);
    }

    public static IReadOnlyList<BackendInfo> EnumerateBackends(string? additionalSearchPath = null)
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

    private static void ConfigureBackendSearchPath(string directory)
    {
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
        CancellationToken token) => new(Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var pin = monoPcm24Khz.Pin();
        var status = QwenNative.ExtractVoiceRef(context, (float*)pin.Pointer, monoPcm24Khz.Length, out var native);
        if (status != 0) throw NativeFailure("qt_extract_voice_ref", status);
        try
        {
            var embedding = new float[native.SpeakerDimension];
            var codes = new int[checked(native.ReferenceLength * native.Codebooks)];
            Marshal.Copy((nint)native.SpeakerEmbedding, embedding, 0, embedding.Length);
            Marshal.Copy((nint)native.Codes, codes, 0, codes.Length);
            return new VoiceReference(embedding, codes, native.ReferenceLength, native.Codebooks, transcript);
        }
        finally
        {
            QwenNative.VoiceRefFree(ref native);
        }
    }, token));

    public Task SynthesizeAsync(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token) =>
        Task.Run(() => Synthesize(request, sink, token, true), CancellationToken.None);

    internal Task SynthesizeAttemptAsync(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token) =>
        Task.Run(() => Synthesize(request, sink, token, false), CancellationToken.None);

    private void Synthesize(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token, bool completeOnFailure)
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

    private static byte OnCancel(void* userData)
    {
        var state = (CallbackState)GCHandle.FromIntPtr((nint)userData).Target!;
        return state.Token.IsCancellationRequested ? (byte)1 : (byte)0;
    }

    private static byte OnChunk(float* samples, int sampleCount, void* userData)
    {
        var state = (CallbackState)GCHandle.FromIntPtr((nint)userData).Target!;
        if (state.Token.IsCancellationRequested) return 0;
        return state.Sink.TryWrite(new ReadOnlySpan<float>(samples, sampleCount)) ? (byte)1 : (byte)0;
    }

    private static void ValidateAbi()
    {
        QwenNative.GetAbiInfo(out var info);
        if (info.AbiVersion != QwenNative.AbiVersion
            || info.InitParamsSize != Marshal.SizeOf<QwenNative.InitParams>()
            || info.TtsParamsSize != Marshal.SizeOf<QwenNative.TtsParams>()
            || info.AudioSize != Marshal.SizeOf<QwenNative.Audio>()
            || info.VoiceRefSize != Marshal.SizeOf<QwenNative.VoiceRef>())
        {
            throw new BadImageFormatException(
                $"qwen ABI mismatch: native v{info.AbiVersion}, init={info.InitParamsSize}, tts={info.TtsParamsSize}; " +
                $"managed v{QwenNative.AbiVersion}, init={Marshal.SizeOf<QwenNative.InitParams>()}, tts={Marshal.SizeOf<QwenNative.TtsParams>()}");
        }
    }

    private static Exception NativeFailure(string operation, int status) =>
        new QwenNativeException(operation, status, Marshal.PtrToStringUTF8((nint)QwenNative.LastError()));

    public ValueTask DisposeAsync()
    {
        var value = Interlocked.Exchange(ref context, 0);
        if (value != 0) QwenNative.Free(value);
        return ValueTask.CompletedTask;
    }

    private sealed class Utf8String : IDisposable
    {
        private nint value;
        internal byte* Pointer => (byte*)value;
        internal Utf8String(string? text) => value = text is null ? 0 : Marshal.StringToCoTaskMemUTF8(text);
        public void Dispose()
        {
            var pointer = Interlocked.Exchange(ref value, 0);
            if (pointer != 0) Marshal.FreeCoTaskMem(pointer);
        }
    }
}
