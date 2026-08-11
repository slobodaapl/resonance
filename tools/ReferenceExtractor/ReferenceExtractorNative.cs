using System.Runtime.InteropServices;

namespace Resonance.Tts;

internal static unsafe partial class QwenNative
{
    [LibraryImport("qwen", EntryPoint = "qt_extract_voice_ref")]
    internal static partial int ExtractVoiceRef(nint context, float* samples, int sampleCount, out VoiceRef voiceRef);

    [LibraryImport("qwen", EntryPoint = "qt_voice_ref_free")]
    internal static partial void VoiceRefFree(ref VoiceRef voiceRef);
}
