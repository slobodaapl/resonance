namespace Resonance.Plugin;

internal interface IProcessLifetimeLease : IDisposable
{
    bool IsPoisoned { get; }
    void Poison(Exception failure);
}

/// <summary>
/// Cross-ALC/process lifetime ownership for Resonance native hooks and model
/// contexts.  Windows uses the named OS mutex as the primary identity; the
/// lock file is retained as the Proton/Unix fallback and as a second process
/// boundary.  A poisoned owner is deliberately rooted until process exit.
/// </summary>
internal sealed class ProcessLifetimeLease : IProcessLifetimeLease
{
    private const string MutexName = "Local\\Resonance.PluginLifetimeOwner";
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(30);
    private static readonly object PoisonGate = new();
    private static readonly List<ProcessLifetimeLease> PoisonedOwners = [];

    private readonly FileStream lockFile;
    private readonly Mutex? namedMutex;
    private readonly bool mutexHeld;
    private readonly object stateGate = new();
    private bool released;
    private bool poisoned;

    private ProcessLifetimeLease(FileStream lockFile, Mutex? namedMutex, bool mutexHeld)
    {
        this.lockFile = lockFile;
        this.namedMutex = namedMutex;
        this.mutexHeld = mutexHeld;
    }

    public bool IsPoisoned
    {
        get
        {
            lock (stateGate) return poisoned;
        }
    }

    public static async ValueTask<ProcessLifetimeLease> AcquireAsync(
        string dataDirectory, CancellationToken token)
    {
        Directory.CreateDirectory(dataDirectory);
        var lockPath = Path.Combine(dataDirectory, "Local_Resonance.PluginLifetimeOwner.lock");
        var deadline = DateTimeOffset.UtcNow + AcquisitionTimeout;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            FileStream? file = null;
            Mutex? mutex = null;
            var mutexHeld = false;
            var acquired = false;
            try
            {
                file = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.SequentialScan);
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        mutex = new Mutex(false, MutexName);
                        try { mutexHeld = mutex.WaitOne(0); }
                        catch (AbandonedMutexException) { mutexHeld = true; }
                        if (!mutexHeld)
                        {
                            mutex.Dispose();
                            mutex = null;
                            file.Dispose();
                            file = null;
                        }
                    }
                    catch (PlatformNotSupportedException)
                    {
                        mutex?.Dispose();
                        mutex = null;
                    }
                }

                if (file is not null && (mutex is null || mutexHeld))
                {
                    acquired = true;
                    return new ProcessLifetimeLease(file, mutex, mutexHeld);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                if (!acquired)
                {
                    if (mutexHeld && mutex is not null)
                    {
                        try { mutex.ReleaseMutex(); }
                        catch (ApplicationException) { }
                    }
                    mutex?.Dispose();
                    mutex = null;
                    file?.Dispose();
                    file = null;
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException(
                    "Resonance native lifetime is still owned by another instance; restart the game before retrying.");
            await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        }
    }

    public void Poison(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (stateGate)
        {
            if (poisoned) return;
            if (released) return;
            poisoned = true;
            lock (PoisonGate)
            {
                if (!PoisonedOwners.Contains(this)) PoisonedOwners.Add(this);
            }
        }
    }

    public void Dispose()
    {
        lock (stateGate)
        {
            if (poisoned || released) return;
            try
            {
                if (mutexHeld && namedMutex is not null)
                {
                    try { namedMutex.ReleaseMutex(); }
                    catch (ApplicationException) { }
                }
                namedMutex?.Dispose();
                lockFile.Dispose();
                released = true;
            }
            catch (Exception error)
            {
                poisoned = true;
                lock (PoisonGate)
                {
                    if (!PoisonedOwners.Contains(this)) PoisonedOwners.Add(this);
                }
                throw new InvalidOperationException(
                    "Resonance native lifetime lease could not be released safely", error);
            }
        }
    }
}

internal static class ProcessLifetimeLeaseProvider
{
    public static async ValueTask<IProcessLifetimeLease> AcquireAsync(
        string dataDirectory, CancellationToken token) =>
        await ProcessLifetimeLease.AcquireAsync(dataDirectory, token).ConfigureAwait(false);
}
