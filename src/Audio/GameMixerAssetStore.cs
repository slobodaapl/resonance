using System.Security.Cryptography;

namespace Resonance.Audio;

public sealed record PublishedGameMixerAsset(
    string ContentHash,
    string VirtualPath,
    string LocalPath,
    long ByteLength);

public sealed class GameMixerAssetStore : IDisposable
{
    public static readonly TimeSpan StartupStaleAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMilliseconds(750);

    private sealed class Entry(PublishedGameMixerAsset asset)
    {
        public PublishedGameMixerAsset Asset { get; } = asset;
        public int Leases;
        public DateTimeOffset? EligibleAfter;
    }

    private readonly object gate = new();
    private readonly string root;
    private readonly string soundRoot;
    private readonly TimeSpan grace;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private int disposed;

    public GameMixerAssetStore(string pluginDataDirectory, TimeSpan? grace = null)
    {
        if (String.IsNullOrWhiteSpace(pluginDataDirectory))
            throw new ArgumentException("Plugin data directory is required", nameof(pluginDataDirectory));
        root = Path.GetFullPath(pluginDataDirectory);
        soundRoot = Path.Combine(root, "sound", "resonance");
        this.grace = grace ?? DefaultGrace;
        if (this.grace < TimeSpan.Zero || this.grace > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(grace));
        Directory.CreateDirectory(soundRoot);
        CleanupStartup(DateTimeOffset.UtcNow);
    }

    public string RootDirectory => root;

    public PublishedGameMixerAsset Publish(ReadOnlyMemory<byte> bytes, string? virtualPath = null)
    {
        if (bytes.IsEmpty) throw new ArgumentException("Cannot publish an empty SCD", nameof(bytes));
        ThrowIfDisposed();
        var hash = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        virtualPath ??= $"sound/resonance/{hash}.scd";
        if (String.IsNullOrWhiteSpace(virtualPath) || Path.IsPathRooted(virtualPath)
            || !virtualPath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("GameMixer virtual path must be a relative SCD game path", nameof(virtualPath));
        var localPath = Path.Combine(soundRoot, hash + ".scd");
        var asset = new PublishedGameMixerAsset(hash, virtualPath, localPath, bytes.Length);
        lock (gate)
        {
            if (entries.TryGetValue(hash, out var existing) && File.Exists(existing.Asset.LocalPath))
                return existing.Asset with { VirtualPath = virtualPath };
            entries.Remove(hash);
            if (!File.Exists(localPath)) PublishAtomically(localPath, bytes.Span);
            entries[hash] = new Entry(asset);
            return asset;
        }
    }

    public bool Retain(string contentHash)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(contentHash, out var entry)) return false;
            checked { entry.Leases++; }
            entry.EligibleAfter = null;
            return true;
        }
    }

    public bool Release(string contentHash, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(contentHash, out var entry) || entry.Leases <= 0) return false;
            entry.Leases--;
            if (entry.Leases == 0) entry.EligibleAfter = now + grace;
            return true;
        }
    }

    public int Cleanup(DateTimeOffset now, int maximum = 16)
    {
        if (maximum <= 0) return 0;
        var removed = 0;
        lock (gate)
        {
            foreach (var pair in entries.Where(pair => pair.Value.Leases == 0
                                                       && pair.Value.EligibleAfter is { } eligible
                                                       && eligible <= now)
                                         .Take(maximum)
                                         .ToArray())
            {
                TryDelete(pair.Value.Asset.LocalPath);
                entries.Remove(pair.Key);
                removed++;
            }
        }
        return removed;
    }

    public void CleanupStartup(DateTimeOffset now, int maximum = 32)
    {
        if (!Directory.Exists(soundRoot)) return;
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(soundRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (removed >= maximum) break;
            var extension = Path.GetExtension(path);
            var lastWrite = File.GetLastWriteTimeUtc(path);
            if (extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
                || now - new DateTimeOffset(lastWrite, TimeSpan.Zero) >= StartupStaleAge)
            {
                TryDelete(path);
                removed++;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lock (gate)
        {
            foreach (var entry in entries.Values)
                if (entry.Leases == 0) TryDelete(entry.Asset.LocalPath);
            entries.Clear();
        }
    }

    private static void PublishAtomically(string destination, ReadOnlySpan<byte> bytes)
    {
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.SequentialScan))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { TryDelete(temporary); }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(GameMixerAssetStore));
    }
}
