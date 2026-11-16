using System.Runtime.InteropServices;
using System.Text;
using Resonance.Tts;

internal sealed unsafe class NativeRuntime(nint context) : IDisposable
{
    internal static NativeRuntime Load(string talker, string codec, string backend, string directory)
    {
        QwenNative.GetAbiInfo(out var abi);
        if (abi.AbiVersion != QwenNative.AbiVersion || abi.InitParamsSize != sizeof(QwenNative.InitParams)
            || abi.TtsParamsSize != sizeof(QwenNative.TtsParams) || abi.VoiceRefSize != sizeof(QwenNative.VoiceRef)
            || abi.AudioSize != sizeof(QwenNative.Audio))
            throw new InvalidDataException("Native ABI layout does not match managed bindings");
        if (QwenNative.BackendLoadFromPath(directory) != 0)
            throw new InvalidOperationException("Backend load failed: " + LastError());
        using var t = new Utf8(talker); using var c = new Utf8(codec); using var b = new Utf8(backend);
        QwenNative.InitDefaultParams(out var parameters);
        parameters.TalkerPath = t.Pointer; parameters.CodecPath = c.Pointer; parameters.BackendName = b.Pointer;
        var handle = QwenNative.Init(ref parameters);
        if (handle == 0) throw new InvalidOperationException("qt_init failed: " + LastError());
        return new(handle);
    }

    internal Generator.Reference Extract(float[] samples)
    {
        QwenNative.VoiceRef reference = default;
        try
        {
            fixed (float* input = samples)
                if (QwenNative.ExtractVoiceRef(context, input, samples.Length, out reference) != 0)
                    throw new InvalidOperationException("Reference extraction failed: " + LastError());
            if (reference.SpeakerDimension is not (1024 or 2048) || reference.Codebooks != 16
                || reference.ReferenceLength is < 1 or > 250)
                throw new InvalidDataException("Native reference shape is invalid");
            var embedding = new float[reference.SpeakerDimension];
            var codes = new int[reference.ReferenceLength * reference.Codebooks];
            Marshal.Copy((nint)reference.SpeakerEmbedding, embedding, 0, embedding.Length);
            Marshal.Copy((nint)reference.Codes, codes, 0, codes.Length);
            return new("", embedding, codes, reference.ReferenceLength, reference.Codebooks);
        }
        finally { QwenNative.VoiceRefFree(ref reference); }
    }

    internal float[] Synthesize(string text, string instruction, long seed)
    {
        using var t = new Utf8(text); using var i = new Utf8(instruction); using var l = new Utf8("english");
        QwenNative.TtsDefaultParams(out var parameters);
        parameters.Text = t.Pointer; parameters.Instruction = i.Pointer; parameters.Language = l.Pointer;
        parameters.Seed = seed; parameters.MaxNewTokens = 220;
        QwenNative.Audio audio = default;
        try
        {
            if (QwenNative.Synthesize(context, ref parameters, out audio) != 0)
                throw new InvalidOperationException("VoiceDesign failed: " + LastError());
            if (audio.SampleRate != 24000 || audio.Channels != 1 || audio.SampleCount <= 0)
                throw new InvalidDataException("VoiceDesign output must be nonempty 24 kHz mono");
            var result = new float[audio.SampleCount];
            Marshal.Copy((nint)audio.Samples, result, 0, result.Length);
            if (result.Any(v => !float.IsFinite(v))) throw new InvalidDataException("VoiceDesign returned non-finite audio");
            return result;
        }
        finally { QwenNative.AudioFree(ref audio); }
    }

    public void Dispose() => QwenNative.Free(context);
    private static string LastError() => Marshal.PtrToStringUTF8((nint)QwenNative.LastError()) ?? "unknown";

    private sealed class Utf8 : IDisposable
    {
        private nint memory;
        internal byte* Pointer => (byte*)memory;
        internal Utf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value + '\0');
            memory = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, memory, bytes.Length);
        }
        public void Dispose()
        {
            if (memory != 0) Marshal.FreeHGlobal(Interlocked.Exchange(ref memory, 0));
        }
    }
}
