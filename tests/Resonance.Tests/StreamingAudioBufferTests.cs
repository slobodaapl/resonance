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
}
