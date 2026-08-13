using NAudio.Wave;
using Resonance.Audio;

namespace Resonance.Tests;

public sealed class BaseCloneCorrectionSampleProviderTests
{
    [Fact]
    public void ImpulseResponseHasConservativeBassCorrectionAndUnityCore()
    {
        var provider = new BaseCloneCorrectionSampleProvider(new ArrayProvider([1f]));
        var response = Drain(provider, 37);

        Assert.Equal(512, response.Length);
        Assert.InRange(GainDecibels(response, 160), -0.86, -0.79);
        Assert.InRange(GainDecibels(response, 1_000), -0.03, 0.03);
        Assert.InRange(response.Sum(), 0.9999, 1.0001);
    }

    [Fact]
    public void ReadChunkingDoesNotChangeFilteredStream()
    {
        float[] input = [0.25f, -0.5f, 0.75f, -1f, 0.125f];

        var oneSampleReads = Drain(new BaseCloneCorrectionSampleProvider(new ArrayProvider(input)), 1);
        var unevenReads = Drain(new BaseCloneCorrectionSampleProvider(new ArrayProvider(input)), 127);

        Assert.Equal(input.Length + 511, oneSampleReads.Length);
        Assert.Equal(oneSampleReads, unevenReads);
    }

    [Fact]
    public void EmptySourceProducesNoSyntheticTail()
    {
        var provider = new BaseCloneCorrectionSampleProvider(new ArrayProvider([]));
        Assert.Equal(0, provider.Read(new float[16], 0, 16));
    }

    private static float[] Drain(ISampleProvider provider, int readSize)
    {
        var samples = new List<float>();
        var buffer = new float[readSize];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        return samples.ToArray();
    }

    private static double GainDecibels(IReadOnlyList<float> impulse, double frequency)
    {
        var real = 0d;
        var imaginary = 0d;
        for (var index = 0; index < impulse.Count; index++)
        {
            var angle = -2d * Math.PI * frequency * index / 24_000d;
            real += impulse[index] * Math.Cos(angle);
            imaginary += impulse[index] * Math.Sin(angle);
        }
        return 20d * Math.Log10(Math.Sqrt(real * real + imaginary * imaginary));
    }

    private sealed class ArrayProvider(float[] samples) : ISampleProvider
    {
        private int index;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(24_000, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var read = Math.Min(count, samples.Length - index);
            samples.AsSpan(index, read).CopyTo(buffer.AsSpan(offset));
            index += read;
            return read;
        }
    }
}
