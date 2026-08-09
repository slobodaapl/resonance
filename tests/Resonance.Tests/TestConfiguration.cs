using Resonance.Tts;

namespace Resonance.Plugin;

public sealed class Configuration
{
    public ComputePreference Compute { get; set; } = ComputePreference.AutoPerformance;
    public string? DesiredBackendName { get; set; }
    public string? DesiredBackendDescription { get; set; }
    public BackendType? DesiredBackendType { get; set; }
    public Resonance.Bootstrap.BackendBenchmarkCache? BackendBenchmark { get; set; }
}


public enum ComputePreference
{
    AutoPerformance,
    AutoEfficiency,
    Manual,
}
