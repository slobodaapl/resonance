using Resonance.Scheduling;

namespace Resonance.Audio;

public interface IGameResourceOverride : IDisposable
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    bool TryRegister(string virtualPath, string localPath, out string? error);
    void Unregister(string virtualPath);
}

public interface IGameMixerSoundPlayer
{
    Task<nint> PlayAsync(string virtualPath, CancellationToken token);
    Task StopAsync(nint playback);
}

public interface IGameMixerAudioBackend : IDisposable
{
    bool IsAvailable { get; }
    bool IsHealthy { get; }
    string Diagnostic { get; }
    Task PrepareAsync(DubLine line, float volume, CancellationToken token);
    void Play(
        DubLine line,
        float volume,
        Action<DubLine> started,
        Action<DubLine> finished,
        Action<DubLine, Exception> failed);
    void Stop(bool discardPrepared = true);
}

public sealed record NativeScdTemplate(string GamePath, byte[] Bytes);

public sealed class FfxivGameMixerAudioBackend : IGameMixerAudioBackend
{
    public static readonly TimeSpan DefaultOutputDrainGuard = TimeSpan.FromMilliseconds(200);

    private sealed record Playback(long Generation, DubLine Line, CancellationTokenSource Cancellation)
    {
        public nint NativePlayback;
        public Task? StopTask;
        public Task? RunTask;
    }

    private readonly object gate = new();
    private readonly IGameResourceOverride resourceOverride;
    private readonly IGameMixerSoundPlayer soundPlayer;
    private readonly GameMixerAssetStore assets;
    private readonly TimeSpan outputDrainGuard;
    private readonly TimeSpan mappingGrace;
    private readonly Func<CancellationToken, Task<NativeScdTemplate>>? loadNativeScdTemplate;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private Playback? playback;
    private readonly Dictionary<DubLine, PreparedPlayback> prepared = [];
    private long generation;
    private int faulted;
    private string diagnostic;
    private int disposed;

    public FfxivGameMixerAudioBackend(
        string pluginDataDirectory,
        IGameResourceOverride resourceOverride,
        IGameMixerSoundPlayer soundPlayer,
        TimeSpan? outputDrainGuard = null,
        TimeSpan? mappingGrace = null,
        Func<CancellationToken, Task<NativeScdTemplate>>? loadNativeScdTemplate = null)
    {
        this.resourceOverride = resourceOverride ?? throw new ArgumentNullException(nameof(resourceOverride));
        this.soundPlayer = soundPlayer ?? throw new ArgumentNullException(nameof(soundPlayer));
        this.outputDrainGuard = outputDrainGuard ?? DefaultOutputDrainGuard;
        this.mappingGrace = mappingGrace ?? GameMixerAssetStore.DefaultGrace;
        this.loadNativeScdTemplate = loadNativeScdTemplate;
        if (this.outputDrainGuard < TimeSpan.Zero || this.outputDrainGuard > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(outputDrainGuard));
        if (this.mappingGrace < TimeSpan.Zero || this.mappingGrace > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(mappingGrace));
        assets = new GameMixerAssetStore(pluginDataDirectory, this.mappingGrace);
        diagnostic = resourceOverride.IsAvailable
            ? "Resonance SCD resource override ready"
            : resourceOverride.UnavailableReason ?? "Resonance SCD resource override unavailable";
    }

    public bool IsAvailable => Volatile.Read(ref disposed) == 0 && resourceOverride.IsAvailable;
    public bool IsHealthy => IsAvailable && Volatile.Read(ref faulted) == 0;
    public string Diagnostic
    {
        get
        {
            if (Volatile.Read(ref faulted) != 0) return Volatile.Read(ref diagnostic);
            return resourceOverride.IsAvailable
                ? "Resonance SCD resource override ready"
                : resourceOverride.UnavailableReason ?? "Resonance SCD resource override unavailable";
        }
    }

    public void Play(
        DubLine line,
        float volume,
        Action<DubLine> started,
        Action<DubLine> finished,
        Action<DubLine, Exception> failed)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(finished);
        ArgumentNullException.ThrowIfNull(failed);
        Stop(discardPrepared: false);
        if (Volatile.Read(ref disposed) != 0)
        {
            FailLine(line, new ObjectDisposedException(nameof(FfxivGameMixerAudioBackend)), failed);
            return;
        }
        if (!IsHealthy)
        {
            FailLine(line, new InvalidOperationException(Diagnostic), failed);
            return;
        }
        if (!line.TryTransition(
                DubLineState.Active,
                DubLineState.Predicted,
                DubLineState.VoiceResolving,
                DubLineState.Queued,
                DubLineState.Generating,
                DubLineState.Buffered))
            return;

        CancellationTokenSource cancellation;
        Playback current;
        lock (gate)
        {
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                line.Token, lifetimeCancellation.Token);
            current = new(++generation, line, cancellation);
            playback = current;
            current.RunTask = Task.Run(() => RunPlaybackAsync(current, volume, started, finished, failed));
        }
    }

    private sealed record PreparedPlayback(
        PublishedGameMixerAsset Asset,
        double DurationSeconds,
        bool MappingAdded);

    public async Task PrepareAsync(DubLine line, float volume, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!IsHealthy) throw new InvalidOperationException(Diagnostic);
        lock (gate)
        {
            if (prepared.ContainsKey(line))
            {
                line.PlaybackAssetReady = true;
                return;
            }
        }
        var source = await DrainProducerAsync(line, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var pcm = GameMixerPcmEncoder.PrepareMono44100(
            source, line.ApplyBaseCloneCorrection, Math.Clamp(volume, 0f, 2f));
        var template = loadNativeScdTemplate is null
            ? null
            : await loadNativeScdTemplate(token).ConfigureAwait(false);
        var scd = template is null
            ? ScdFileBuilder.Build(pcm.Samples, pcm.SampleRate)
            : ScdFileBuilder.BuildFromNativeTemplate(pcm.Samples, template.Bytes, pcm.SampleRate);
        var virtualPath = template is null
            ? $"sound/resonance/resonance-{scd.ContentHash}-{line.SessionEpoch:x}-{line.Sequence:x}.scd"
            : CreateTemplateSiblingPath(template.GamePath, scd.ContentHash, line.Sequence);
        var asset = assets.Publish(scd.Bytes, virtualPath);
        if (!assets.Retain(asset.ContentHash))
            throw new InvalidOperationException("Published GameMixer asset could not be retained");
        var mappingAdded = false;
        try
        {
            if (!resourceOverride.TryRegister(asset.VirtualPath, asset.LocalPath, out var addError))
            {
                MarkFaulted(addError ?? "Resonance rejected the temporary SCD resource override");
                throw new InvalidOperationException(Diagnostic);
            }
            mappingAdded = true;
            lock (gate)
            {
                if (Volatile.Read(ref disposed) != 0 || line.IsTerminal)
                    throw new OperationCanceledException(token);
                prepared[line] = new(asset, pcm.DurationSeconds, mappingAdded);
                line.PlaybackAssetReady = true;
            }
        }
        catch
        {
            if (mappingAdded) resourceOverride.Unregister(asset.VirtualPath);
            assets.Release(asset.ContentHash, DateTimeOffset.UtcNow);
            throw;
        }
    }

    public void Stop(bool discardPrepared = true)
    {
        Playback? current;
        lock (gate)
        {
            current = playback;
            if (current is { NativePlayback: not 0 } active)
                current.StopTask = StopNativePlaybackAsync(active.NativePlayback);
            playback = null;
            generation++;
        }
        if (current is not null)
        {
            try { current.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        if (discardPrepared) ReleasePreparedAssets();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Playback? current;
        lock (gate) current = playback;
        Stop();
        try { lifetimeCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        if (current?.RunTask is { } runTask)
        {
            try { runTask.GetAwaiter().GetResult(); }
            catch (Exception error) { MarkFaulted($"FFXIV game-mixer shutdown failed: {error.Message}"); }
        }
        resourceOverride.Dispose();
        assets.Dispose();
        lifetimeCancellation.Dispose();
    }

    private void ReleasePreparedAssets()
    {
        PreparedPlayback[] stale;
        lock (gate)
        {
            stale = prepared.Values.ToArray();
            prepared.Clear();
        }
        foreach (var item in stale)
        {
            if (item.MappingAdded) resourceOverride.Unregister(item.Asset.VirtualPath);
            assets.Release(item.Asset.ContentHash, DateTimeOffset.UtcNow);
        }
        assets.Cleanup(DateTimeOffset.UtcNow);
    }

    private async Task RunPlaybackAsync(
        Playback current,
        float volume,
        Action<DubLine> started,
        Action<DubLine> finished,
        Action<DubLine, Exception> failed)
    {
        PublishedGameMixerAsset? asset = null;
        var mappingAdded = false;
        try
        {
            PreparedPlayback ready;
            lock (gate) prepared.TryGetValue(current.Line, out ready!);
            if (ready is null)
            {
                await PrepareAsync(current.Line, volume, current.Cancellation.Token).ConfigureAwait(false);
                lock (gate) ready = prepared[current.Line];
            }
            lock (gate) prepared.Remove(current.Line);
            asset = ready.Asset;
            mappingAdded = ready.MappingAdded;
            diagnostic = $"Resonance mapped {asset.VirtualPath} to {asset.LocalPath} ({asset.ByteLength} bytes)";
            current.Cancellation.Token.ThrowIfCancellationRequested();
            // The native call is intentionally made at volume 1.0. Configured
            // plugin/game gain is already applied to the encoded PCM, while
            // the SCD is categorized as Voice by the native player.
            current.NativePlayback = await soundPlayer.PlayAsync(
                asset.VirtualPath, current.Cancellation.Token).ConfigureAwait(false);
            if (current.NativePlayback == 0)
                throw new InvalidOperationException("FFXIV SoundManager rejected the Resonance SCD");
            if (!resourceOverride.IsAvailable)
                throw new InvalidOperationException(
                    resourceOverride.UnavailableReason ?? "Resonance SCD resource redirect failed");
            current.Cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(current)) return;
            started(current.Line);
            var completionDelay = TimeSpan.FromSeconds(ready.DurationSeconds) + outputDrainGuard;
            if (completionDelay > TimeSpan.Zero)
                await Task.Delay(completionDelay, current.Cancellation.Token).ConfigureAwait(false);
            if (!IsCurrent(current)) return;
            if (current.Line.TryTransition(DubLineState.Completed, DubLineState.Active))
            {
                finished(current.Line);
                current.Line.Dispose();
            }
        }
        catch (OperationCanceledException) when (current.Cancellation.IsCancellationRequested)
        {
            // Stop/new Play/disposal owns cancellation. Never publish a stale
            // Finished event or clear state belonging to the replacement line.
        }
        catch (Exception error)
        {
            if (mappingAdded && !resourceOverride.IsAvailable)
            {
                var detail = asset is null
                    ? error.Message
                    : $"{error.Message}; mapping {asset.VirtualPath} -> {asset.LocalPath} ({asset.ByteLength} bytes)";
                if (asset is not null)
                {
                    try { File.Copy(asset.LocalPath, asset.LocalPath + ".failed", true); }
                    catch (Exception preserveError) when (preserveError is IOException or UnauthorizedAccessException) { }
                }
                MarkFaulted(detail);
            }
            if (IsCurrent(current))
            {
                current.Line.TryTransition(
                    DubLineState.Failed,
                    DubLineState.Active,
                    DubLineState.Buffered,
                    DubLineState.Generating,
                    DubLineState.Queued,
                    DubLineState.VoiceResolving,
                    DubLineState.Predicted);
                failed(current.Line, error);
                current.Line.Dispose();
            }
        }
        finally
        {
            if (current.StopTask is { } stopTask)
            {
                try { await stopTask.ConfigureAwait(false); }
                catch (Exception) { /* StopNativePlaybackAsync records diagnostics. */ }
            }
            if (asset is not null)
            {
                assets.Release(asset.ContentHash, DateTimeOffset.UtcNow);
                _ = ReleaseMappingAfterGraceAsync(asset, mappingAdded);
            }
            lock (gate)
            {
                if (IsCurrentLocked(current))
                {
                    playback = null;
                }
            }
            current.Cancellation.Dispose();
        }
    }

    private async Task<float[]> DrainProducerAsync(DubLine line, CancellationToken token)
    {
        var samples = new List<float>();
        line.Audio.MarkConsumerStarted();
        await foreach (var chunk in line.Audio.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            samples.AddRange(chunk.Samples.Span);
            chunk.Dispose();
            if (samples.Count > GameMixerPcmEncoder.SourceSampleRate * GameMixerPcmEncoder.MaxDurationSeconds)
                throw new InvalidDataException("GameMixer producer exceeded the bounded duration limit");
        }
        if (!line.Audio.ProducerCompleted)
            throw new InvalidDataException("GameMixer producer ended without completion");
        return samples.ToArray();
    }

    internal static string CreateTemplateSiblingPath(string templatePath, string contentHash, long generation)
    {
        var normalized = templatePath.Replace('\\', '/').TrimStart('/');
        var separator = normalized.LastIndexOf('/');
        var directory = separator < 0 ? String.Empty : normalized[..(separator + 1)];
        return $"{directory}resonance-{contentHash}-{generation:x}.scd";
    }

    private async Task ReleaseMappingAfterGraceAsync(PublishedGameMixerAsset asset, bool mappingAdded)
    {
        try { await Task.Delay(mappingGrace, lifetimeCancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { return; }
        if (mappingAdded) resourceOverride.Unregister(asset.VirtualPath);
        assets.Cleanup(DateTimeOffset.UtcNow);
    }

    private async Task StopNativePlaybackAsync(nint playback)
    {
        try
        {
            await soundPlayer.StopAsync(playback)
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception error) { MarkFaulted($"FFXIV sound stop failed: {error.Message}"); }
    }

    private bool IsCurrent(Playback candidate)
    {
        lock (gate) return IsCurrentLocked(candidate) && Volatile.Read(ref disposed) == 0;
    }

    private bool IsCurrentLocked(Playback candidate) => ReferenceEquals(playback, candidate)
        && candidate.Generation == generation;

    private void FailLine(DubLine line, Exception error, Action<DubLine, Exception> failed)
    {
        line.TryTransition(
            DubLineState.Failed,
            DubLineState.Active,
            DubLineState.Buffered,
            DubLineState.Generating,
            DubLineState.Queued,
            DubLineState.VoiceResolving,
            DubLineState.Predicted);
        failed(line, error);
        line.Dispose();
    }

    private void MarkFaulted(string reason)
    {
        Volatile.Write(ref faulted, 1);
        Volatile.Write(ref diagnostic, reason);
    }
}
