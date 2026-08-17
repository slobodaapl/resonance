using Resonance.Bootstrap;
using Resonance.Plugin;

namespace Resonance.Tests;

public sealed class QualityPresetTests
{
    [Theory]
    [InlineData(QualityPreset.Q4Base06B, "base-q4", "tokenizer-q4", "voicedesign-q4")]
    [InlineData(QualityPreset.Q8Base06B, "base-q8", "tokenizer-q8", "voicedesign-q8")]
    [InlineData(QualityPreset.Q4Base17B, "base-1.7b-q4", "tokenizer-q4", "voicedesign-q4")]
    [InlineData(QualityPreset.Q8Base17B, "base-1.7b-q8", "tokenizer-q8", "voicedesign-q8")]
    public void PresetSelectsMatchingBaseAndSharedQuantizationAssets(
        QualityPreset preset, string expectedBase, string expectedTokenizer, string expectedVoiceDesign)
    {
        var actual = ModelQualitySelection.Resolve(preset);

        Assert.Equal((expectedBase, expectedTokenizer, expectedVoiceDesign), actual);
    }

    [Fact]
    public void ExistingSerializedPresetValuesRetainTheirModelSizeAndQuantization()
    {
        Assert.Equal(0, (int)QualityPreset.Q4Base06B);
        Assert.Equal(1, (int)QualityPreset.Q8Base06B);
    }
}
