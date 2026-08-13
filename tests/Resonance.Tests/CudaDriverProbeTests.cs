using Resonance.Bootstrap;

namespace Resonance.Tests;

public sealed class CudaDriverProbeTests
{
    [Fact]
    public void NativeDriverExportIsAccepted()
    {
        Assert.True(CudaDriverProbe.IsAvailable(true));
    }

    [Fact]
    public void DriverWithoutLoadableExportIsRejected()
    {
        Assert.False(CudaDriverProbe.IsAvailable(false));
    }
}
