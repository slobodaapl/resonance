using Resonance.Bootstrap;
using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class BackendSelectorTests
{
    private static readonly BackendInfo Cpu = new("CPU", "Host CPU", BackendType.Cpu, 0, 0, 0);
    private static readonly BackendInfo Cuda = new("CUDA0", "RTX", BackendType.Cuda, 1, 8, 12);

    [Fact]
    public void MissingManualDeviceFallsBackWithoutOverwritingIntent()
    {
        var configuration = new Configuration
        {
            Compute = ComputePreference.Manual,
            DesiredBackendName = "Vulkan1",
            DesiredBackendDescription = "Old GPU",
            DesiredBackendType = BackendType.Vulkan,
        };

        var selected = new BackendSelector().Select(configuration, [Cpu, Cuda]);

        Assert.True(selected.IsTemporaryCpuFallback);
        Assert.Equal("CPU", selected.Effective.Name);
        Assert.Equal("Vulkan1", selected.Desired.Name);
        Assert.Equal("Vulkan1", configuration.DesiredBackendName);
    }

    [Fact]
    public void RememberedDeviceIsRestoredWhenItReturns()
    {
        var configuration = new Configuration
        {
            Compute = ComputePreference.Manual,
            DesiredBackendName = "CUDA0",
            DesiredBackendDescription = "RTX",
            DesiredBackendType = BackendType.Cuda,
        };

        var selected = new BackendSelector().Select(configuration, [Cpu, Cuda]);

        Assert.False(selected.IsTemporaryCpuFallback);
        Assert.Equal(Cuda, selected.Effective);
    }

    [Fact]
    public void ExplicitChoiceReplacesRememberedUnavailableDevice()
    {
        var configuration = new Configuration
        {
            Compute = ComputePreference.Manual,
            DesiredBackendName = "Vulkan1",
        };

        BackendSelector.SetDesired(configuration, Cuda);

        Assert.Equal("CUDA0", configuration.DesiredBackendName);
        Assert.Equal("RTX", configuration.DesiredBackendDescription);
        Assert.Equal(BackendType.Cuda, configuration.DesiredBackendType);
    }

    [Fact]
    public void MatchingBenchmarkWinnerOverridesBootstrapHeuristic()
    {
        var vulkan = new BackendInfo("Vulkan0", "Integrated GPU", BackendType.Vulkan, 2, 4, 8);
        var configuration = new Configuration
        {
            Compute = ComputePreference.AutoPerformance,
            BackendBenchmark = new("identity", vulkan.Name, DateTimeOffset.UtcNow, []),
        };

        var selected = new BackendSelector().Select(configuration, [Cpu, Cuda, vulkan], "identity");

        Assert.Equal(vulkan, selected.Effective);
    }

    [Fact]
    public void StaleBenchmarkWinnerIsIgnored()
    {
        var vulkan = new BackendInfo("Vulkan0", "Integrated GPU", BackendType.Vulkan, 2, 4, 8);
        var configuration = new Configuration
        {
            Compute = ComputePreference.AutoPerformance,
            BackendBenchmark = new("old", vulkan.Name, DateTimeOffset.UtcNow, []),
        };

        var selected = new BackendSelector().Select(configuration, [Cpu, Cuda, vulkan], "new");

        Assert.Equal(Cuda, selected.Effective);
    }
}
