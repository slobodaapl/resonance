using Resonance.Tts;

namespace Resonance.Tests;

public sealed class VoiceEmbeddingTests
{
    [Fact]
    public void LinearBlendUsesTheRequestedProportionWithoutNormalizingIdentity()
    {
        float[] foundation = [0, 4, -2];
        float[] flavor = [8, 0, 6];
        Assert.Equal([2f, 3f, 0f], VoiceEmbedding.LinearBlend(foundation, flavor, 0.25));
        Assert.Equal(foundation, VoiceEmbedding.LinearBlend(foundation, flavor, 0));
        Assert.Equal(flavor, VoiceEmbedding.LinearBlend(foundation, flavor, 1));
        Assert.Equal([0f, 4f, -2f], foundation);
        Assert.Equal([8f, 0f, 6f], flavor);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidWeightsAreRejected(double weight) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VoiceEmbedding.LinearBlend([1], [2], weight));

    [Fact]
    public void InvalidEmbeddingShapesAndValuesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => VoiceEmbedding.LinearBlend([], [], 0.5));
        Assert.Throws<ArgumentException>(() => VoiceEmbedding.LinearBlend([1], [1, 2], 0.5));
        Assert.Throws<ArgumentException>(() => VoiceEmbedding.LinearBlend([float.NaN], [1], 0.5));
        Assert.Throws<ArgumentException>(() => VoiceEmbedding.LinearBlend([1], [float.PositiveInfinity], 0.5));
    }
}
