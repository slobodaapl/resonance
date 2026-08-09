using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Bootstrap;

public sealed class RuntimeManager : ITtsRuntime
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly BackendSelector selector = new();
    private readonly Configuration configuration;
    private readonly Action saveConfiguration;
    private readonly string talkerPath;
    private readonly string codecPath;
    private readonly string designModelHash;
    private readonly string runtimeVersion;
    private readonly string runtimePackDirectory;
    private readonly HashSet<string> unhealthy = [];
    private QwenCppRuntime? runtime;

    public string ModelHash { get; }
    public string BenchmarkIdentity { get; private set; } = string.Empty;
    public BackendSelection? Selection { get; private set; }
    public IReadOnlyList<BackendInfo> DetectedBackends { get; private set; } = [];
    public ITtsRuntime Runtime => this;
    public RuntimeCapabilities Capabilities => runtime?.Capabilities
        ?? throw new InvalidOperationException("TTS runtime is not ready");
    public event Action<BackendSelection>? SelectionChanged;

    public RuntimeManager(Configuration configuration, Action saveConfiguration, string talkerPath, string codecPath,
        string modelHash, string designModelHash, string runtimeVersion, string runtimePackDirectory)
    {
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;
        this.talkerPath = talkerPath;
        this.codecPath = codecPath;
        ModelHash = modelHash;
        this.designModelHash = designModelHash;
        this.runtimeVersion = runtimeVersion;
        this.runtimePackDirectory = runtimePackDirectory;
    }

    public async Task InitializeAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            DetectedBackends = await Task.Run(() => QwenCppRuntime.EnumerateBackends(runtimePackDirectory), token).ConfigureAwait(false);
            BenchmarkIdentity = BackendBenchmark.Identity(runtimeVersion, ModelHash, designModelHash, configuration.Compute, DetectedBackends);
            var selection = selector.Select(configuration, DetectedBackends, BenchmarkIdentity);
            try
            {
                await ReplaceRuntime(selection.Effective.Name, token).ConfigureAwait(false);
                Selection = selection;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                unhealthy.Add(selection.Effective.Name);
                Selection = selection;
                if (!await TryFallbackAsync(error, token).ConfigureAwait(false)) throw;
            }
            SelectionChanged?.Invoke(Selection!);
        }
        finally { gate.Release(); }
    }

    public async Task SetDesiredAsync(BackendInfo backend, CancellationToken token)
    {
        if (!DetectedBackends.Any(candidate => candidate.Name == backend.Name))
            throw new ArgumentException("Device is not in the detected backend list", nameof(backend));

        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Explicit user action overrides any remembered unavailable device.
            BackendSelector.SetDesired(configuration, backend);
            saveConfiguration();
            unhealthy.Remove(backend.Name);
            Selection = new(backend, backend, false, null);
            try { await ReplaceRuntime(backend.Name, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                unhealthy.Add(backend.Name);
                if (!await TryFallbackAsync(error, token).ConfigureAwait(false)) throw;
            }
            SelectionChanged?.Invoke(Selection);
        }
        finally { gate.Release(); }
    }

    public async Task RefreshBackendsAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var detected = await Task.Run(() => QwenCppRuntime.EnumerateBackends(runtimePackDirectory), token)
                .ConfigureAwait(false);
            var identity = BackendBenchmark.Identity(
                runtimeVersion, ModelHash, designModelHash, configuration.Compute, detected);
            var selection = selector.Select(configuration, detected, identity);
            DetectedBackends = detected;
            BenchmarkIdentity = identity;
            if (Selection?.Effective.Name != selection.Effective.Name)
            {
                try { await ReplaceRuntime(selection.Effective.Name, token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    unhealthy.Add(selection.Effective.Name);
                    Selection = selection;
                    if (!await TryFallbackAsync(error, token).ConfigureAwait(false)) throw;
                    SelectionChanged?.Invoke(Selection!);
                    return;
                }
            }
            Selection = selection;
            SelectionChanged?.Invoke(selection);
        }
        finally { gate.Release(); }
    }

    private async Task ReplaceRuntime(string backendName, CancellationToken token)
    {
        var replacement = await Task.Run(() => new QwenCppRuntime(talkerPath, codecPath, backendName), token).ConfigureAwait(false);
        var previous = runtime;
        runtime = replacement;
        if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
    }

    public async Task BenchmarkAndApplyAsync(string designPath, CancellationToken token)
    {
        if (configuration.Compute == ComputePreference.Manual) return;
        var cached = configuration.BackendBenchmark;
        if (cached is null || cached.Identity != BenchmarkIdentity)
        {
            cached = await BackendBenchmark.RunAsync(BenchmarkIdentity, designPath, codecPath,
                DetectedBackends, configuration.Compute, token).ConfigureAwait(false);
            configuration.BackendBenchmark = cached;
            saveConfiguration();
        }
        var winner = DetectedBackends.FirstOrDefault(candidate => candidate.Name == cached.WinnerName);
        if (winner is null || configuration.Compute == ComputePreference.Manual) return;

        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Selection?.Effective.Name == winner.Name) return;
            unhealthy.Remove(winner.Name);
            try
            {
                await ReplaceRuntime(winner.Name, token).ConfigureAwait(false);
                Selection = new(winner, winner, false, null);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                unhealthy.Add(winner.Name);
                Selection = new(winner, winner, false, null);
                if (!await TryFallbackAsync(error, token).ConfigureAwait(false)) throw;
            }
            SelectionChanged?.Invoke(Selection!);
        }
        finally { gate.Release(); }
    }

    public async ValueTask<VoiceReference> ExtractReferenceAsync(
        ReadOnlyMemory<float> monoPcm24Khz,
        string transcript,
        CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var active = runtime ?? throw new InvalidOperationException("TTS runtime is not ready");
                try { return await active.ExtractReferenceAsync(monoPcm24Khz, transcript, token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException && IsBackendFailure(error))
                {
                    unhealthy.Add(Selection!.Effective.Name);
                    if (!await TryFallbackAsync(error, token).ConfigureAwait(false)) throw;
                    SelectionChanged?.Invoke(Selection!);
                }
            }
        }
        finally { gate.Release(); }
    }

    public async Task SynthesizeAsync(SynthesisRequest request, Resonance.Audio.StreamingAudioBuffer sink, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var active = runtime ?? throw new InvalidOperationException("TTS runtime is not ready");
                try
                {
                    await active.SynthesizeAttemptAsync(request, sink, token).ConfigureAwait(false);
                    return;
                }
                catch (Exception error) when (error is not OperationCanceledException && IsBackendFailure(error))
                {
                    unhealthy.Add(Selection!.Effective.Name);
                    var consumerStarted = sink.ConsumerStarted;
                    sink.DiscardBuffered();
                    if (!await TryFallbackAsync(error, token).ConfigureAwait(false)) throw;
                    SelectionChanged?.Invoke(Selection!);
                    if (consumerStarted) throw;
                }
            }
        }
        finally { gate.Release(); }
    }

    private async Task<bool> TryFallbackAsync(Exception cause, CancellationToken token)
    {
        foreach (var candidate in DetectedBackends
                     .Where(candidate => !unhealthy.Contains(candidate.Name))
                     .OrderBy(candidate => candidate.Type switch
                     {
                         BackendType.Cuda => 0,
                         BackendType.Vulkan => 1,
                         BackendType.Gpu or BackendType.Accelerator => 2,
                         BackendType.Cpu => 3,
                         _ => 4,
                     }))
        {
            try
            {
                await ReplaceRuntime(candidate.Name, token).ConfigureAwait(false);
                var desired = Selection?.Desired ?? candidate;
                var error = $"Inference backend '{Selection?.Effective.Description ?? desired.Description}' failed: {cause.Message}. " +
                            $"Using '{candidate.Description}' for this session; the configured device remains remembered.";
                Selection = new(desired, candidate, candidate.Type == BackendType.Cpu, error);
                return true;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                unhealthy.Add(candidate.Name);
                cause = error;
            }
        }
        return false;
    }

    private static bool IsBackendFailure(Exception error)
    {
        if (error is QwenNativeException) return true;
        var message = error.ToString();
        return message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
            || message.Contains("OOM", StringComparison.OrdinalIgnoreCase)
            || message.Contains("allocation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("device lost", StringComparison.OrdinalIgnoreCase)
            || message.Contains("backend", StringComparison.OrdinalIgnoreCase)
            || message.Contains("CUDA", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Vulkan", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
            runtime = null;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }
}
