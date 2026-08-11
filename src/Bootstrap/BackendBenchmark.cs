using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Resonance.Audio;
using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Bootstrap;

public sealed record BackendBenchmarkMeasurement(
    string BackendName,
    bool Successful,
    double? InitializationSeconds,
    double? TimeToFirstAudioSeconds,
    double? RealTimeFactor,
    string? Error);

public sealed class BackendBenchmarkCleanupException : InvalidOperationException
{
    public BackendBenchmarkCleanupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class BackendBenchmark
{
    private const string Text = "A quiet lantern glows beside the harbor.";
    private const string Instruction = "A calm, clear adult narrator voice with natural conversational pacing.";
    private static readonly object FailedRuntimeGate = new();
    private static readonly List<QwenCppRuntime> TerminalFailedRuntimes = [];

    public static string Identity(string runtimeVersion, string baseHash, string designHash, ComputePreference preference,
        IReadOnlyList<BackendInfo> backends)
    {
        var devices = string.Join('\n', backends.OrderBy(value => value.Name).Select(value =>
            $"{value.Name}|{value.Type}|{value.DeviceIndex}|{value.Description}|{value.MemoryTotal}"));
        var input = $"v2\n{runtimeVersion}\n{baseHash}\n{designHash}\n{preference}\n{RuntimeEnvironmentIdentity.Get()}\n{devices}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    internal static async Task<BackendBenchmarkCache> RunAsync(string identity, string designPath, string codecPath,
        IReadOnlyList<BackendInfo> backends, ComputePreference preference, CancellationToken token,
        IProcessLifetimeLease pluginLifetimeLease)
    {
        ArgumentNullException.ThrowIfNull(pluginLifetimeLease);
        if (pluginLifetimeLease.IsPoisoned)
            throw new BackendBenchmarkCleanupException(
                "The plugin process lifetime lease is poisoned; restart is required",
                new InvalidOperationException("Native benchmark construction is unavailable"));
        var measurements = new List<BackendBenchmarkMeasurement>();
        foreach (var backend in backends)
        {
            token.ThrowIfCancellationRequested();
            if (pluginLifetimeLease.IsPoisoned)
                throw new BackendBenchmarkCleanupException(
                    "The plugin process lifetime lease became poisoned during benchmarking",
                    new InvalidOperationException("Native benchmark ownership is no longer valid"));
            var initialization = Stopwatch.StartNew();
            QwenCppRuntime? runtime = null;
            Task? synthesis = null;
            Exception? inferenceFailure = null;
            try
            {
                runtime = new QwenCppRuntime(designPath, codecPath, backend.Name,
                    ownsProcessLease: false, pluginLifetimeLease: pluginLifetimeLease);
                initialization.Stop();
                using var audio = new StreamingAudioBuffer();
                var generation = Stopwatch.StartNew();
                synthesis = runtime.SynthesizeAsync(
                    new(Text, "english", null, Instruction, 0x5245534f4e414e43, 192), audio, token);
                double? firstAudio = null;
                var sampleCount = 0;
                await foreach (var chunk in audio.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    firstAudio ??= generation.Elapsed.TotalSeconds;
                    sampleCount += chunk.Count;
                    chunk.Dispose();
                }
                await synthesis.ConfigureAwait(false);
                generation.Stop();
                var audioSeconds = sampleCount / 24000d;
                if (firstAudio is null || audioSeconds < 0.2) throw new InvalidDataException("Benchmark produced no usable audio");
                measurements.Add(new(backend.Name, true, initialization.Elapsed.TotalSeconds, firstAudio.Value,
                    generation.Elapsed.TotalSeconds / audioSeconds, null));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (pluginLifetimeLease.IsPoisoned)
                    throw new BackendBenchmarkCleanupException(
                        "The plugin process lifetime lease became poisoned during benchmarking", error);
                inferenceFailure = error;
                measurements.Add(new(backend.Name, false, initialization.Elapsed.TotalSeconds,
                    null, null, error.Message));
            }
            finally
            {
                Exception? synthesisCleanupFailure = null;
                if (synthesis is not null)
                {
                    try { await synthesis.ConfigureAwait(false); }
                    catch (Exception error) { synthesisCleanupFailure = error; }
                }
                if (runtime is not null)
                {
                    try
                    {
                        await runtime.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupError)
                    {
                        lock (FailedRuntimeGate)
                        {
                            if (!TerminalFailedRuntimes.Contains(runtime)) TerminalFailedRuntimes.Add(runtime);
                        }
                        var failures = new List<Exception>();
                        if (inferenceFailure is not null) failures.Add(inferenceFailure);
                        if (synthesisCleanupFailure is not null
                            && !ReferenceEquals(synthesisCleanupFailure, inferenceFailure))
                            failures.Add(synthesisCleanupFailure);
                        failures.Add(cleanupError);
                        throw new BackendBenchmarkCleanupException(
                            $"Backend '{backend.Name}' runtime cleanup failed; restart is required",
                            failures.Count == 1 ? failures[0] : new AggregateException(failures));
                    }
                }
            }
        }

        var winner = SelectWinner(backends, measurements, preference)
            ?? throw new InvalidOperationException("No inference backend passed the deterministic benchmark");
        return new(identity, winner.Name, DateTimeOffset.UtcNow, measurements);
    }

    public static BackendInfo? SelectWinner(IReadOnlyList<BackendInfo> backends,
        IReadOnlyList<BackendBenchmarkMeasurement> measurements, ComputePreference preference)
    {
        var successful = measurements.Where(IsUsable)
            .Join(backends, value => value.BackendName, value => value.Name, (measurement, backend) => (measurement, backend))
            .ToArray();
        if (successful.Length == 0) return null;
        if (preference == ComputePreference.AutoEfficiency)
        {
            var realtime = successful.Where(value => value.measurement.RealTimeFactor is { } rtf
                                                     && rtf < 0.8).ToArray();
            if (realtime.Length > 0)
                successful = realtime;
        }
        return successful.MinBy(value => Score(value.measurement, preference, value.backend.Type)).backend;
    }

    private static double Score(BackendBenchmarkMeasurement value, ComputePreference preference, BackendType type)
    {
        if (value.TimeToFirstAudioSeconds is not { } firstAudio
            || value.RealTimeFactor is not { } realtime
            || value.InitializationSeconds is not { } initialization
            || !double.IsFinite(firstAudio)
            || !double.IsFinite(realtime)
            || !double.IsFinite(initialization))
            throw new InvalidDataException($"Backend '{value.BackendName}' has no usable benchmark score");
        var measured = firstAudio * 2 + realtime + initialization * 0.05;
        if (preference != ComputePreference.AutoEfficiency) return measured;
        var efficiencyBias = type switch
        {
            BackendType.Accelerator => -0.50,
            BackendType.Vulkan or BackendType.Gpu => -0.15,
            BackendType.Cpu => 0.10,
            _ => 0,
        };
        return measured + efficiencyBias;
    }

    private static bool IsUsable(BackendBenchmarkMeasurement value) =>
        value.Successful
        && value.InitializationSeconds is double initialization
        && value.TimeToFirstAudioSeconds is double firstAudio
        && value.RealTimeFactor is double realtime
        && double.IsFinite(initialization)
        && double.IsFinite(firstAudio)
        && double.IsFinite(realtime);
}
