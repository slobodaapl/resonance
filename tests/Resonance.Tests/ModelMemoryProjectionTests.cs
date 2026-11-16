using Resonance.Bootstrap;
using Resonance.Tts;
using Resonance.UI;

namespace Resonance.Tests;

public sealed class ModelMemoryProjectionTests
{
    private const double GiB = 1073741824d;

    [Fact]
    public void BeforeLoadingAddsWeightsAndReportsRemainingHeadroom()
    {
        var projection = ModelMemoryProjection.Create(Bytes(23.6), Bytes(3.8), 0, (long)Bytes(1.94));
        Assert.InRange(projection.Other, 19.8 * GiB - 4, 19.8 * GiB + 4);
        Assert.InRange(projection.Free, 1.86 * GiB - 4, 1.86 * GiB + 4);
        Assert.InRange(projection.Projected, 21.74 * GiB - 4, 21.74 * GiB + 4);
        Assert.True(projection.NearCapacity);
        Assert.Equal(0, projection.Overflow);
    }

    [Fact]
    public void IncludedWeightsAreReplacedRatherThanCountedTwice()
    {
        var current = ModelMemoryProjection.Create(Bytes(24), Bytes(4), (long)Bytes(2), (long)Bytes(2));
        Assert.Equal(18 * GiB, current.Other);
        Assert.Equal(4 * GiB, current.Free);
        var hovered = ModelMemoryProjection.Create(Bytes(24), Bytes(4), (long)Bytes(2), (long)Bytes(1));
        Assert.Equal(5 * GiB, hovered.Free);
        Assert.Equal(4 * GiB, current.Free);
    }

    [Fact]
    public void OverflowAndCapacityBoundaryRemainVisible()
    {
        var overflow = ModelMemoryProjection.Create(Bytes(10), Bytes(1), 0, (long)Bytes(2));
        Assert.Equal(0, overflow.Free);
        Assert.Equal(GiB, overflow.Overflow);
        Assert.True(overflow.NearCapacity);
        Assert.False(ModelMemoryProjection.Create(1000, 101, 0, 0).NearCapacity);
        Assert.True(ModelMemoryProjection.Create(1000, 100, 0, 0).NearCapacity);
        Assert.Equal(0, ModelMemoryProjection.Create(100, 200, 1000, 10).Other);
    }

    [Fact]
    public void SnapshotDistinguishesResidentDeviceFromAnotherIdenticalGpu()
    {
        var cuda = Device("CUDA0", BackendType.Cuda, "0000:01:00.0");
        var vulkan = Device("Vulkan0", BackendType.Vulkan, "0000:01:00.0");
        var other = Device("CUDA1", BackendType.Cuda, "0000:02:00.0");
        var snapshot = new DeviceMemorySnapshot(DateTimeOffset.UtcNow, [cuda, vulkan, other], cuda.Name, cuda.PhysicalDeviceId, 100);
        Assert.True(snapshot.IncludesModel(cuda));
        Assert.True(snapshot.IncludesModel(vulkan));
        Assert.False(snapshot.IncludesModel(other));
        Assert.False((snapshot with { ResidentWeightsBytes = 0 }).IncludesModel(cuda));
        Assert.False((snapshot with { ResidentPhysicalDeviceId = null }).IncludesModel(vulkan));
        Assert.False((snapshot with { ResidentPhysicalDeviceId = null }).CanDetermineInclusion(vulkan));
    }

    private static ulong Bytes(double gib) => (ulong)(gib * GiB);
    private static BackendInfo Device(string name, BackendType type, string? physicalId) =>
        new(name, "Identical GPU name", type, 0, 1, 10, physicalId);
}
