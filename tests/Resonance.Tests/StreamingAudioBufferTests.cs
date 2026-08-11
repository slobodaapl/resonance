using Resonance.Audio;

namespace Resonance.Tests;

public sealed class StreamingAudioBufferTests
{
    [Fact]
    public async Task CopiesProducerMemoryAndPreservesChunkOrder()
    {
        using var buffer = new StreamingAudioBuffer();
        var source = new[] { 1f, 2f, 3f };
        Assert.True(buffer.TryWrite(source));
        source[0] = 99f;
        Assert.True(buffer.TryWrite([4f]));
        buffer.Complete();

        var result = new List<float>();
        await foreach (var chunk in buffer.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            result.AddRange(chunk.Samples.ToArray());
            chunk.Dispose();
        }

        Assert.Equal([1f, 2f, 3f, 4f], result);
    }

    [Fact]
    public async Task CaptureReceivesIdenticalCompletedAndStreamingSamples()
    {
        using var buffer = new StreamingAudioBuffer();
        using var capture = buffer.CreateCapture();
        Assert.True(buffer.TryWrite([1f, 2f]));
        Assert.True(buffer.TryWrite([3f]));
        buffer.Complete();

        var playback = await buffer.DrainAsync(TestContext.Current.CancellationToken);
        var cached = await capture.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1f, 2f, 3f], playback);
        Assert.Equal(playback, cached);
        Assert.False(buffer.TryWrite([4f]));
    }

    [Fact]
    public void BufferedSamplesExcludePlaybackConsumption()
    {
        using var buffer = new StreamingAudioBuffer();
        Assert.True(buffer.TryWrite(new float[24_000]));
        buffer.ReportConsumed(6_000);

        Assert.Equal(18_000, buffer.BufferedSamples);
    }

    [Fact]
    public async Task AsyncWriterWaitsForCapacityAndInvalidationReleasesIt()
    {
        using var buffer = new StreamingAudioBuffer(maxBufferedSeconds: 1d / 24_000, maxBufferedBytes: sizeof(float));
        Assert.True(buffer.TryWrite([1f]));

        using var cancellation = new CancellationTokenSource();
        var blocked = buffer.WriteAsync(new float[] { 2f }, cancellation.Token).AsTask();
        Assert.False(blocked.IsCompleted);

        buffer.Invalidate();
        Assert.False(await blocked);
    }

    [Fact]
    public async Task ConfiguredCaptureDoesNotSilentlyTruncateLongLine()
    {
        const int sampleCount = 31 * 24_000;
        using var buffer = new StreamingAudioBuffer(
            maxBufferedSeconds: 32,
            maxBufferedBytes: sampleCount * sizeof(float) + 4096);
        using var capture = buffer.CreateCapture(sampleCount * sizeof(float) + 4096);
        Assert.True(buffer.TryWrite(new float[sampleCount]));
        buffer.Complete();

        var playback = await buffer.DrainAsync(TestContext.Current.CancellationToken);
        var cached = await capture.DrainAsync(TestContext.Current.CancellationToken);

        Assert.False(capture.Overflowed);
        Assert.Equal(sampleCount, playback.Length);
        Assert.Equal(playback.Length, cached.Length);
    }

    [Fact]
    public void RejectedParentChunkMarksCaptureBeforeProducerCompletes()
    {
        using var buffer = new StreamingAudioBuffer(maxBufferedSeconds: 1d / 24_000, maxBufferedBytes: sizeof(float));
        using var capture = buffer.CreateCapture(sizeof(float));
        Assert.True(buffer.TryWrite([1f]));
        Assert.False(buffer.TryWrite([2f]));
        Assert.True(capture.Overflowed);
        buffer.Complete();
    }

    [Fact]
    public void DroppedParentChunkMarksCaptureOverflow()
    {
        using var buffer = new StreamingAudioBuffer(maxBufferedSeconds: 1d / 24_000, maxBufferedBytes: sizeof(float));
        using var capture = buffer.CreateCapture(sizeof(float));
        Assert.True(buffer.TryWrite([1f]));

        buffer.Invalidate();

        Assert.True(capture.Overflowed);
    }
}
