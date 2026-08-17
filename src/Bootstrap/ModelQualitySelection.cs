using Resonance.Plugin;

namespace Resonance.Bootstrap;

internal static class ModelQualitySelection
{
    internal static (string Base, string Tokenizer, string VoiceDesign) Resolve(QualityPreset quality) =>
        quality switch
        {
            QualityPreset.Q4Base06B => ("base-q4", "tokenizer-q4", "voicedesign-q4"),
            QualityPreset.Q8Base06B => ("base-q8", "tokenizer-q8", "voicedesign-q8"),
            QualityPreset.Q4Base17B => ("base-1.7b-q4", "tokenizer-q4", "voicedesign-q4"),
            QualityPreset.Q8Base17B => ("base-1.7b-q8", "tokenizer-q8", "voicedesign-q8"),
            _ => throw new InvalidDataException($"Unknown quality preset '{quality}'"),
        };
}
