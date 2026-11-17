using Resonance.Audio;

namespace Resonance.Tts;

public sealed record RuntimeCapabilities(
    bool Streaming,
    bool Cancellation,
    bool VoiceReferenceExtraction,
    IReadOnlyList<BackendInfo> Backends);

public sealed record BackendInfo(
    string Name,
    string Description,
    BackendType Type,
    int DeviceIndex,
    ulong MemoryFree,
    ulong MemoryTotal,
    string? PhysicalDeviceId = null);

public enum BackendType { Unknown, Cpu, Cuda, Vulkan, Gpu, Accelerator }

public sealed record VoiceReference(float[] SpeakerEmbedding, int[] RvqCodes, int RvqLength, int Codebooks, string Transcript);

public sealed record SynthesisRequest(
    string Text,
    string Language,
    VoiceReference? Reference,
    string? Instruction,
    long Seed,
    int MaxNewTokens = 2048)
{
    public SynthesisRequest WithoutDialogueCues() => this with
    {
        Text = DialogueCues.SpeechText(Text),
        Reference = Reference is { } reference && DialogueCues.Contains(reference.Transcript)
            ? reference with { Transcript = DialogueCues.SpeechText(reference.Transcript) } : Reference,
    };
}

public interface ITtsRuntime : IAsyncDisposable
{
    RuntimeCapabilities Capabilities { get; }
    ValueTask<VoiceReference> ExtractReferenceAsync(ReadOnlyMemory<float> monoPcm24Khz, string transcript, CancellationToken token);
    Task SynthesizeAsync(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token);
}

