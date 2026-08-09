using Resonance.Bootstrap;

namespace Resonance.Tests;

public sealed class CudaDriverProbeTests
{
    [Fact]
    public void NativeDriverExportIsAccepted()
    {
        Assert.True(CudaDriverProbe.IsAvailable(true, false, false));
    }

    [Fact]
    public void WineBridgeFileIsAcceptedWhenStubHidesExports()
    {
        Assert.True(CudaDriverProbe.IsAvailable(false, true, true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WineFallbackRequiresBothWineAndBridgeFile(bool isWineRuntime, bool bridgeFileExists)
    {
        Assert.False(CudaDriverProbe.IsAvailable(false, isWineRuntime, bridgeFileExists));
    }
}
