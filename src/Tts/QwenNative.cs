using System.Runtime.InteropServices;

namespace Resonance.Tts;

internal static unsafe partial class QwenNative
{
    internal const int AbiVersion = 6;
    private const string Library = "qwen";

    [StructLayout(LayoutKind.Sequential)]
    internal struct AbiInfo
    {
        internal uint AbiVersion;
        internal uint AbiMinVersion;
        internal uint InitParamsSize;
        internal uint TtsParamsSize;
        internal uint AudioSize;
        internal uint VoiceRefSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VoiceRef
    {
        internal float* SpeakerEmbedding;
        internal int SpeakerDimension;
        internal int* Codes;
        internal int ReferenceLength;
        internal int Codebooks;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InitParams
    {
        internal int AbiVersion;
        internal byte* TalkerPath;
        internal byte* CodecPath;
        internal byte UseFlashAttention;
        internal byte ClampFp16;
        internal int MaxBatch;
        internal float CodecChunkSeconds;
        internal byte* BackendName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Audio
    {
        internal float* Samples;
        internal int SampleCount;
        internal int SampleRate;
        internal int Channels;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate byte CancelCallback(void* userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate byte AudioChunkCallback(float* samples, int sampleCount, void* userData);

    [StructLayout(LayoutKind.Sequential)]
    internal struct TtsParams
    {
        internal int AbiVersion;
        internal byte* Text;
        internal byte* Language;
        internal byte* Instruction;
        internal byte* Speaker;
        internal float* ReferenceAudio;
        internal int ReferenceSampleCount;
        internal byte* ReferenceText;
        internal long Seed;
        internal int MaxNewTokens;
        internal byte DoSample;
        internal float Temperature;
        internal int TopK;
        internal float TopP;
        internal float RepetitionPenalty;
        internal byte SubtalkerDoSample;
        internal float SubtalkerTemperature;
        internal int SubtalkerTopK;
        internal float SubtalkerTopP;
        internal byte* DumpDirectory;
        internal nint Cancel;
        internal void* CancelUserData;
        internal nint OnChunk;
        internal void* OnChunkUserData;
        internal float* ReferenceSpeakerEmbedding;
        internal int ReferenceSpeakerDimension;
        internal int* ReferenceCodes;
        internal int ReferenceLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeBackendInfo
    {
        internal byte* Name;
        internal byte* Description;
        internal int Type;
        internal int DeviceIndex;
        internal ulong MemoryFree;
        internal ulong MemoryTotal;
    }

    [LibraryImport(Library, EntryPoint = "qt_get_abi_info")]
    internal static partial void GetAbiInfo(out AbiInfo info);
    [LibraryImport(Library, EntryPoint = "qt_backend_count")]
    internal static partial int BackendCount();
    [LibraryImport(Library, EntryPoint = "qt_backend_load_from_path", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int BackendLoadFromPath(string path);
    [LibraryImport(Library, EntryPoint = "qt_backend_get_info")]
    internal static partial int BackendGetInfo(int index, out NativeBackendInfo info);
    [LibraryImport(Library, EntryPoint = "qt_init_default_params")]
    internal static partial void InitDefaultParams(out InitParams parameters);
    [LibraryImport(Library, EntryPoint = "qt_tts_default_params")]
    internal static partial void TtsDefaultParams(out TtsParams parameters);
    [LibraryImport(Library, EntryPoint = "qt_init")]
    internal static partial nint Init(ref InitParams parameters);
    [LibraryImport(Library, EntryPoint = "qt_free")]
    internal static partial void Free(nint context);
    [LibraryImport(Library, EntryPoint = "qt_synthesize")]
    internal static partial int Synthesize(nint context, ref TtsParams parameters, out Audio audio);
    [LibraryImport(Library, EntryPoint = "qt_audio_free")]
    internal static partial void AudioFree(ref Audio audio);
    [LibraryImport(Library, EntryPoint = "qt_last_error")]
    internal static partial byte* LastError();
}
