using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Bootstrap;

public sealed record BackendSelection(
    BackendInfo Desired,
    BackendInfo Effective,
    bool IsTemporaryCpuFallback,
    string? Error,
    bool NotifyError = true);

public sealed class BackendSelector
{
    public BackendSelection Select(Configuration configuration, IReadOnlyList<BackendInfo> detected, string? benchmarkIdentity = null)
    {
        if (detected.Count == 0) throw new InvalidOperationException("qwentts reported no inference backends");
        var cpu = detected.FirstOrDefault(candidate => candidate.Type == BackendType.Cpu)
            ?? throw new InvalidOperationException("qwentts CPU fallback is unavailable");

        if (configuration.Compute != ComputePreference.Manual || configuration.DesiredBackendName is null)
        {
            var cached = configuration.BackendBenchmark;
            if (cached is not null && cached.Identity == benchmarkIdentity)
            {
                var winner = detected.FirstOrDefault(candidate => candidate.Name == cached.WinnerName);
                if (winner is not null) return new(winner, winner, false, null);
            }
            var automatic = ChooseAutomatic(configuration.Compute, detected, cpu);
            return new(automatic, automatic, false, null);
        }

        var desired = detected.FirstOrDefault(candidate => candidate.Name == configuration.DesiredBackendName)
            ?? detected.FirstOrDefault(candidate =>
                candidate.Type == configuration.DesiredBackendType
                && candidate.Description == configuration.DesiredBackendDescription);
        if (desired is not null)
        {
            var failedMeasurement = configuration.BackendBenchmark is { } benchmark
                                    && benchmark.Identity == benchmarkIdentity
                ? benchmark.Measurements.FirstOrDefault(value =>
                    value.BackendName == desired.Name && !value.Successful)
                : null;
            if (failedMeasurement is null) return new(desired, desired, false, null);
            return new(
                desired,
                cpu,
                true,
                $"Configured inference device '{desired.Description}' previously failed validation: " +
                $"{failedMeasurement.Error}. Resonance is using CPU until you explicitly select or benchmark it again.",
                false);
        }

        var remembered = new BackendInfo(
            configuration.DesiredBackendName,
            configuration.DesiredBackendDescription ?? configuration.DesiredBackendName,
            configuration.DesiredBackendType ?? BackendType.Unknown,
            -1, 0, 0);
        return new(
            remembered,
            cpu,
            true,
            $"Configured inference device '{remembered.Description}' ({remembered.Name}) is unavailable. " +
            $"Resonance is using CPU for this session and will retry the configured device next launch.");
    }

    public static void SetDesired(Configuration configuration, BackendInfo backend)
    {
        configuration.Compute = ComputePreference.Manual;
        configuration.DesiredBackendName = backend.Name;
        configuration.DesiredBackendDescription = backend.Description;
        configuration.DesiredBackendType = backend.Type;
        if (configuration.BackendBenchmark?.Measurements.Any(value =>
                value.BackendName == backend.Name && !value.Successful) == true)
            configuration.BackendBenchmark = null;
    }

    private static BackendInfo ChooseAutomatic(ComputePreference preference, IReadOnlyList<BackendInfo> detected, BackendInfo cpu)
    {
        if (preference == ComputePreference.AutoEfficiency)
            return detected.FirstOrDefault(candidate => candidate.Type == BackendType.Accelerator)
                ?? detected.FirstOrDefault(candidate => candidate.Type == BackendType.Vulkan)
                ?? cpu;

        return detected.FirstOrDefault(candidate => candidate.Type == BackendType.Cuda)
            ?? detected.FirstOrDefault(candidate => candidate.Type == BackendType.Vulkan)
            ?? detected.FirstOrDefault(candidate => candidate.Type == BackendType.Gpu)
            ?? cpu;
    }
}
