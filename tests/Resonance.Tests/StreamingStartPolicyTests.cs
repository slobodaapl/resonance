using Resonance.Scheduling;

namespace Resonance.Tests;

public sealed class StreamingStartPolicyTests
{
    [Fact]
    public void CompletedAudioStartsImmediately()
    {
        Assert.True(StreamingStartPolicy.ShouldStart(true, 0.1, 10, 5, 0));
    }

    [Fact]
    public void BufferCoveringEstimatedRemainderStartsImmediately()
    {
        Assert.True(StreamingStartPolicy.ShouldStart(false, 3, 2.9, 5, 0));
    }

    [Fact]
    public void RealtimeGenerationWaitsOneSecond()
    {
        Assert.False(StreamingStartPolicy.ShouldStart(false, 0.5, 2, 1, 0.99));
        Assert.True(StreamingStartPolicy.ShouldStart(false, 0.5, 2, 1, 1));
    }

    [Fact]
    public void SlowUncoveredGenerationDoesNotStream()
    {
        Assert.False(StreamingStartPolicy.ShouldStart(false, 0.5, 2, 1.01, 10));
    }

    [Fact]
    public void EmptyBufferNeverStarts()
    {
        Assert.False(StreamingStartPolicy.ShouldStart(true, 0, 0, 0.5, 10));
    }
}
