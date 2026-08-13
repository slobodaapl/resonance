using Resonance.Bootstrap;

namespace Resonance.Tests;

public sealed class RuntimeEnvironmentIdentityTests
{
    [Fact]
    public void NativeWindowsDoesNotRequireProtonCudaVariables()
    {
        var missing = RuntimeEnvironmentIdentity.MissingProtonCudaVariables(false, _ => null);

        Assert.Empty(missing);
    }

    [Fact]
    public void WineRequiresBothProtonCudaVariablesSetToOne()
    {
        var values = new Dictionary<string, string?>
        {
            ["PROTON_ENABLE_NVAPI"] = "1",
            ["PROTON_NVIDIA_NVCUDA"] = "true",
        };

        var missing = RuntimeEnvironmentIdentity.MissingProtonCudaVariables(true, name => values[name]);

        Assert.Equal(["PROTON_NVIDIA_NVCUDA"], missing);
    }

    [Fact]
    public void WineWithBothProtonCudaVariablesEnabledNeedsNoWarning()
    {
        var missing = RuntimeEnvironmentIdentity.MissingProtonCudaVariables(true, _ => "1");

        Assert.Empty(missing);
    }
}
