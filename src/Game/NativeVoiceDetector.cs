using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Resonance.Plugin;

namespace Resonance.Game;

public sealed record NativeVoiceObservation(
    string ScdPath,
    DateTimeOffset StartedAt,
    uint? SoundNumber = null,
    OfficialVoiceClipObservation? CorrelatedClip = null);

public sealed partial class NativeVoiceDetector : IDisposable
{
    private static readonly object ProcessOwnerGate = new();
    private static NativeVoiceDetector? retainedFailureOwner;
    private static readonly TimeSpan UncorrelatedClipHold = TimeSpan.FromSeconds(1);
    private const string LoadSoundFileSignature = "E8 ?? ?? ?? ?? 48 85 C0 75 05 40 B7 F6";
    private const string PlaySpecificSoundSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 33 F6 8B DA 48 8B F9 0F BA E2 0F";
    private const string PlayClipSoundSignature =
        "E8 ?? ?? ?? ?? 48 85 C0 74 ?? 48 89 43 ?? 48 8B 4B ?? 48 85 C9 74 ?? ?? ?? ?? FF 50 ?? 84 C0 75";

    private delegate nint LoadSoundFileDelegate(nint resourceHandle, uint argument);
    private delegate nint PlaySpecificSoundDelegate(nint sound, int argument);
    private delegate nint PlayClipSoundDelegate(nint manager, nint path, float volume, uint fadeInDuration,
        float x, float y, float z, float speed, int priority, uint soundNumber, byte autoRelease, byte argument12);

    private readonly object gate = new();
    private readonly HashSet<nint> knownPointers = [];
    private readonly Dictionary<nint, string> paths = [];
    private readonly Dictionary<string, List<OfficialVoiceClipObservation>> recentOfficialClips =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DateTimeOffset> recentNativeClipStarts = [];
    private readonly ConcurrentQueue<Action> pendingCallbacks = new();
    private readonly ThreadLocal<HookCallbackContext?> callbackContexts = new();
    private readonly IPluginLog log;
    private readonly IProcessLifetimeLease lifetimeLease;
    private Hook<LoadSoundFileDelegate>? loadHook;
    private Hook<PlaySpecificSoundDelegate>? playHook;
    private Hook<PlayClipSoundDelegate>? clipHook;
    // Keep the captured trampolines independent of the mutable Hook fields.
    // A late callback must still forward to the game after shutdown has
    // closed admission, and must never turn a nulled Hook reference into a
    // native return value of zero.
    private LoadSoundFileDelegate? originalLoadSoundFile;
    private PlaySpecificSoundDelegate? originalPlaySpecificSound;
    private PlayClipSoundDelegate? originalPlayClipSound;
    private int callbackDrainScheduled;
    private int disposed;
    private readonly object hookLifecycleGate = new();
    private int inFlightHookCallbacks;
    private bool shutdownRequested;
    private bool callbackEntryClosed;
    private bool hookResourcesDisposed;
    private Task? deferredDispose;
    private TaskCompletionSource? hookDisposalCompletion;

    private sealed class HookCallbackContext
    {
        public int Depth;
        public OfficialVoiceClipObservation? CurrentClip;
    }

    private sealed class HookCallbackLease : IDisposable
    {
        private NativeVoiceDetector? owner;
        private readonly HookCallbackContext context;

        public HookCallbackLease(NativeVoiceDetector owner, HookCallbackContext context, bool forwardOnly)
        {
            this.owner = owner;
            this.context = context;
            ForwardOnly = forwardOnly;
        }

        public bool ForwardOnly { get; }

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ExitHookCallback(context);
    }

    private static readonly int ResourceDataOffset = Marshal.SizeOf<ResourceHandle>();
    private static readonly int SoundDataOffset = Marshal.SizeOf<nint>();

    public bool IsAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }
    public event Action<NativeVoiceObservation>? TalkVoiceStarted;
    public event Action<OfficialVoiceClipObservation>? OfficialVoiceClipObserved;

    private HookCallbackLease? EnterHookCallback()
    {
        lock (hookLifecycleGate)
        {
            if (hookResourcesDisposed || callbackEntryClosed) return null;
            inFlightHookCallbacks++;
            var context = callbackContexts.Value ??= new HookCallbackContext();
            context.Depth++;
            return new HookCallbackLease(this, context, shutdownRequested);
        }
    }

    private void ExitHookCallback(HookCallbackContext context)
    {
        if (context.Depth > 0) context.Depth--;
        lock (hookLifecycleGate)
        {
            if (inFlightHookCallbacks > 0) inFlightHookCallbacks--;
            Monitor.PulseAll(hookLifecycleGate);
        }
    }

    private bool DisposeHookResources(out Exception? failure)
    {
        failure = null;
        lock (hookLifecycleGate)
        {
            if (hookResourcesDisposed) return true;
        }

        // Disable first. Hook implementations may wait for callbacks that
        // entered before the disable barrier; no hook object is freed until
        // those callbacks have left their Original trampoline.
        var errors = new List<Exception>();
        var hooksDisabled = true;
        try { loadHook?.Disable(); }
        catch (Exception error) { hooksDisabled = false; errors.Add(error); log.Warning(error, "Native VO load hook disable failed"); }
        try { playHook?.Disable(); }
        catch (Exception error) { hooksDisabled = false; errors.Add(error); log.Warning(error, "Native VO playback hook disable failed"); }
        try { clipHook?.Disable(); }
        catch (Exception error) { hooksDisabled = false; errors.Add(error); log.Warning(error, "Native VO clip hook disable failed"); }

        lock (hookLifecycleGate)
        {
            // Disable has completed successfully for every installed hook.
            // Close callback admission before checking quiescence so no new
            // counted callback can race the zero check and hook disposal.
            callbackEntryClosed = true;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (inFlightHookCallbacks != 0 && DateTime.UtcNow < deadline)
                Monitor.Wait(hookLifecycleGate, TimeSpan.FromMilliseconds(50));
            if (inFlightHookCallbacks != 0 || !hooksDisabled)
            {
                // Do not free a detour/trampoline whose disable barrier or
                // callback quiescence was not proven.  Keeping the native
                // objects alive is safer than turning a late callback into a
                // use-after-free; the process-level owner will reclaim them.
                log.Error("Native VO hooks could not be quiesced safely; retaining native hook resources");
                failure = errors.Count == 0
                    ? new InvalidOperationException("Native VO hook callbacks did not quiesce before disposal")
                    : new AggregateException("Native VO hook disable failed", errors);
                return false;
            }
        }

        var hooksDisposed = true;
        try { loadHook?.Dispose(); }
        catch (Exception error) { hooksDisposed = false; errors.Add(error); log.Warning(error, "Native VO load hook disposal failed"); }
        try { playHook?.Dispose(); }
        catch (Exception error) { hooksDisposed = false; errors.Add(error); log.Warning(error, "Native VO playback hook disposal failed"); }
        try { clipHook?.Dispose(); }
        catch (Exception error) { hooksDisposed = false; errors.Add(error); log.Warning(error, "Native VO clip hook disposal failed"); }
        if (!hooksDisposed)
        {
            // Retain every managed hook reference when a native Dispose call
            // fails.  The shutdown gate still rejects new callbacks, while
            // retaining the trampoline owners prevents an unload-time call
            // through a freed or collected delegate.
            log.Error("Native VO hook disposal was incomplete; retaining hook ownership");
            failure = new AggregateException("Native VO hook disposal failed", errors);
            return false;
        }
        // Complete managed callback cleanup before dropping native hook
        // references.  If this fails, the retry path still owns every hook
        // needed to keep a late trampoline safe.
        callbackContexts.Dispose();
        lock (gate)
        {
            knownPointers.Clear();
            paths.Clear();
            recentOfficialClips.Clear();
            recentNativeClipStarts.Clear();
        }
        while (pendingCallbacks.TryDequeue(out _)) { }
        loadHook = null;
        playHook = null;
        clipHook = null;
        lock (hookLifecycleGate) hookResourcesDisposed = true;
        ReleaseFailedProcessOwner();
        return true;
    }

    /// <summary>
    /// Returns a bounded conservative hold for a recent native cutscene clip
    /// that has not yet correlated to a Talk line. It prevents synthetic audio
    /// overlap without declaring an unrelated Talk line native-voiced.
    /// </summary>
    public DateTimeOffset? GetActiveOfficialClipHoldUntil(DateTimeOffset talkObservedAt)
    {
        lock (gate)
        {
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
            DateTimeOffset? latest = null;
            foreach (var clips in recentOfficialClips.Values)
            {
                clips.RemoveAll(clip => clip.StartedAt < cutoff);
                foreach (var clip in clips)
                {
                    if (clip.StartedAt < talkObservedAt - TimeSpan.FromMilliseconds(250)) continue;
                    if (latest is null || clip.StartedAt > latest.Value) latest = clip.StartedAt;
                }
            }
            recentNativeClipStarts.RemoveAll(startedAt => startedAt < cutoff);
            foreach (var startedAt in recentNativeClipStarts)
            {
                if (startedAt < talkObservedAt - TimeSpan.FromMilliseconds(250)) continue;
                if (latest is null || startedAt > latest.Value) latest = startedAt;
            }
            return latest?.Add(UncorrelatedClipHold);
        }
    }

    internal NativeVoiceDetector(ISigScanner scanner, IGameInteropProvider interop, IPluginLog log,
        IProcessLifetimeLease lifetimeLease)
    {
        ArgumentNullException.ThrowIfNull(lifetimeLease);
        this.log = log;
        this.lifetimeLease = lifetimeLease;
        try
        {
            if (!scanner.TryScanText(LoadSoundFileSignature, out var loadAddress))
                throw new InvalidOperationException("sound resource-load signature not found");
            if (!scanner.TryScanText(PlaySpecificSoundSignature, out var playAddress))
                throw new InvalidOperationException("sound playback signature not found");
            loadHook = interop.HookFromAddress<LoadSoundFileDelegate>(loadAddress, OnLoadSoundFile);
            playHook = interop.HookFromAddress<PlaySpecificSoundDelegate>(playAddress, OnPlaySpecificSound);
            originalLoadSoundFile = loadHook.Original;
            originalPlaySpecificSound = playHook.Original;
            loadHook.Enable();
            playHook.Enable();
            if (scanner.TryScanText(PlayClipSoundSignature, out var clipAddress))
            {
                clipHook = interop.HookFromAddress<PlayClipSoundDelegate>(clipAddress, OnPlayClipSound);
                originalPlayClipSound = clipHook.Original;
                clipHook.Enable();
            }
            else throw new InvalidOperationException(
                "higher-level cutscene VO hook unavailable; native VO correlation is disabled");
            IsAvailable = true;
        }
        catch (Exception error)
        {
            UnavailableReason = error.Message;
            Interlocked.Exchange(ref disposed, 1);
            lock (hookLifecycleGate) shutdownRequested = true;
            if (!DisposeHookResources(out var cleanupFailure))
            {
                var failure = cleanupFailure is null ? error : new AggregateException(error, cleanupFailure);
                UnavailableReason = $"{error.Message}; restart required because native hook cleanup failed";
                RetainFailedProcessOwner(failure);
            }
            log.Warning(error, "Native VO correlation unavailable; synthetic playback remains enabled");
        }
    }

    private nint OnPlayClipSound(nint manager, nint pathPointer, float volume, uint fadeInDuration,
        float x, float y, float z, float speed, int priority, uint soundNumber, byte autoRelease, byte argument12)
    {
        using var callback = EnterHookCallback();
        if (callback is null)
        {
            var original = originalPlayClipSound;
            return original is null
                ? 0
                : original(manager, pathPointer, volume, fadeInDuration, x, y, z, speed,
                    priority, soundNumber, autoRelease, argument12);
        }
        if (callback.ForwardOnly)
            return originalPlayClipSound!(manager, pathPointer, volume, fadeInDuration, x, y, z, speed,
                priority, soundNumber, autoRelease, argument12);
        return OnPlayClipSoundCore(manager, pathPointer, volume, fadeInDuration, x, y, z, speed,
            priority, soundNumber, autoRelease, argument12);
    }

    private nint OnPlayClipSoundCore(nint manager, nint pathPointer, float volume, uint fadeInDuration,
        float x, float y, float z, float speed, int priority, uint soundNumber, byte autoRelease, byte argument12)
    {
        OfficialVoiceClipObservation? observation = null;
        try
        {
            var path = Marshal.PtrToStringUTF8(pathPointer);
            if (path is not null && CutsceneVoicePath().IsMatch(path))
            {
                observation = new OfficialVoiceClipObservation(path, soundNumber, DateTimeOffset.UtcNow);
                PublishOfficialClip(observation);
            }
        }
        catch (Exception error)
        {
            observation = null;
            log.Warning(error, "Official voice clip observation failed");
        }

        nint result;
        var context = callbackContexts.Value;
        var previousClipObservation = context?.CurrentClip;
        if (observation is not null && context is not null) context.CurrentClip = observation;
        try
        {
            // The high-level observation is published before Original. The
            // game can synchronously enter the low-level sound hook here;
            // that callback must see the exact path/sound correlation.
            result = originalPlayClipSound!(manager, pathPointer, volume, fadeInDuration, x, y, z, speed,
                priority, soundNumber, autoRelease, argument12);
        }
        catch
        {
            if (observation is not null) RemoveOfficialClip(observation);
            throw;
        }
        finally { if (context is not null) context.CurrentClip = previousClipObservation; }
        if (observation is not null)
            EnqueueCallback(() => OfficialVoiceClipObserved?.Invoke(observation));
        return result;
    }

    private unsafe nint OnLoadSoundFile(nint resourceHandle, uint argument)
    {
        using var callback = EnterHookCallback();
        if (callback is null)
        {
            var original = originalLoadSoundFile;
            return original is null ? 0 : original(resourceHandle, argument);
        }
        if (callback.ForwardOnly) return originalLoadSoundFile!(resourceHandle, argument);
        return OnLoadSoundFileCore(resourceHandle, argument);
    }

    private unsafe nint OnLoadSoundFileCore(nint resourceHandle, uint argument)
    {
        var result = originalLoadSoundFile!(resourceHandle, argument);
        try
        {
            var path = ((ResourceHandle*)resourceHandle)->FileName.ToString();
            if (!path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)) return result;
            var data = Marshal.ReadIntPtr(resourceHandle + ResourceDataOffset);
            if (data == 0) return result;
            var voice = CutsceneVoicePath().IsMatch(path) && !IgnoredPath().IsMatch(path);
            lock (gate)
            {
                if (voice) { knownPointers.Add(data); paths[data] = path; }
                else { knownPointers.Remove(data); paths.Remove(data); }
            }
        }
        catch (Exception error) { log.Warning(error, "Native VO resource observation failed"); }
        return result;
    }

    private nint OnPlaySpecificSound(nint sound, int argument)
    {
        using var callback = EnterHookCallback();
        if (callback is null)
        {
            var original = originalPlaySpecificSound;
            return original is null ? 0 : original(sound, argument);
        }
        if (callback.ForwardOnly) return originalPlaySpecificSound!(sound, argument);
        return OnPlaySpecificSoundCore(sound, argument);
    }

    private nint OnPlaySpecificSoundCore(nint sound, int argument)
    {
        var result = originalPlaySpecificSound!(sound, argument);
        try
        {
            var data = Marshal.ReadIntPtr(sound + SoundDataOffset);
            var startedAt = DateTimeOffset.UtcNow;
            string? path;
            var correlatedClip = callbackContexts.Value?.CurrentClip;
            lock (gate)
                path = knownPointers.Contains(data) && paths.TryGetValue(data, out var value) ? value : null;
            if (path is not null)
            {
                lock (gate)
                {
                    recentNativeClipStarts.Add(startedAt);
                    recentNativeClipStarts.RemoveAll(value =>
                        value < startedAt - TimeSpan.FromSeconds(2));
                }
                var observation = new NativeVoiceObservation(path, startedAt,
                    correlatedClip?.SoundNumber, correlatedClip);
                EnqueueCallback(() =>
                {
                    var soundNumber = observation.SoundNumber ?? ResolveSoundNumber(observation);
                    TalkVoiceStarted?.Invoke(observation with { SoundNumber = soundNumber });
                });
            }
        }
        catch (Exception error) { log.Warning(error, "Native VO playback observation failed"); }
        return result;
    }

    private uint? ResolveSoundNumber(NativeVoiceObservation observation)
    {
        lock (gate)
        {
            if (!recentOfficialClips.TryGetValue(observation.ScdPath, out var clips)) return null;
            var matches = clips.Where(clip =>
                (observation.StartedAt - clip.StartedAt).Duration() <= TimeSpan.FromMilliseconds(300)).ToArray();
            return matches.Length == 1 ? matches[0].SoundNumber : null;
        }
    }

    private void PublishOfficialClip(OfficialVoiceClipObservation observation)
    {
        lock (gate)
        {
            if (!recentOfficialClips.TryGetValue(observation.ScdPath, out var clips))
                recentOfficialClips[observation.ScdPath] = clips = [];
            clips.Add(observation);
            var cutoff = observation.StartedAt - TimeSpan.FromSeconds(2);
            foreach (var stale in recentOfficialClips
                         .Where(pair =>
                         {
                             pair.Value.RemoveAll(clip => clip.StartedAt < cutoff);
                             return pair.Value.Count == 0;
                         })
                         .Select(pair => pair.Key)
                         .ToArray())
                recentOfficialClips.Remove(stale);
        }
    }

    private void RemoveOfficialClip(OfficialVoiceClipObservation observation)
    {
        lock (gate)
        {
            if (!recentOfficialClips.TryGetValue(observation.ScdPath, out var clips)) return;
            clips.RemoveAll(value => ReferenceEquals(value, observation));
            if (clips.Count == 0) recentOfficialClips.Remove(observation.ScdPath);
        }
    }

    private void EnqueueCallback(Action callback)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        pendingCallbacks.Enqueue(callback);
        if (Interlocked.Exchange(ref callbackDrainScheduled, 1) == 0)
        {
            try { ThreadPool.QueueUserWorkItem(static state => ((NativeVoiceDetector)state!).DrainCallbacks(), this); }
            catch
            {
                Interlocked.Exchange(ref callbackDrainScheduled, 0);
                while (pendingCallbacks.TryDequeue(out _)) { }
            }
        }
    }

    private void DrainCallbacks()
    {
        while (Volatile.Read(ref disposed) == 0 && pendingCallbacks.TryDequeue(out var callback))
        {
            try { callback(); }
            catch (Exception error) { log.Warning(error, "Native VO callback dispatch failed"); }
        }

        Interlocked.Exchange(ref callbackDrainScheduled, 0);
        if (Volatile.Read(ref disposed) == 0 && !pendingCallbacks.IsEmpty
            && Interlocked.Exchange(ref callbackDrainScheduled, 1) == 0)
        {
            try { ThreadPool.QueueUserWorkItem(static state => ((NativeVoiceDetector)state!).DrainCallbacks(), this); }
            catch
            {
                Interlocked.Exchange(ref callbackDrainScheduled, 0);
                while (pendingCallbacks.TryDequeue(out _)) { }
            }
        }
    }

    [GeneratedRegex(@"^cut/.*/(vo_|voice)", RegexOptions.IgnoreCase)]
    private static partial Regex CutsceneVoicePath();

    [GeneratedRegex(@"^(bgcommon|music|sound/(battle|foot|instruments|strm|vfx|voice/Vo_Emote|zingle))/", RegexOptions.IgnoreCase)]
    private static partial Regex IgnoredPath();

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (DisposeHookResources(out failure))
                    {
                        completion.TrySetResult();
                        return;
                    }
                }
                catch (Exception error) { failure = error; }
                if (attempt < 2)
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1))).ConfigureAwait(false);
            }
        }
        catch (Exception error) { failure = error; }

        var terminalFailure = failure ?? new InvalidOperationException("Native VO hook cleanup failed");
        RetainFailedProcessOwner(terminalFailure);
        completion.TrySetException(terminalFailure);
    }

    private void RetainFailedProcessOwner(Exception failure)
    {
        lock (ProcessOwnerGate) retainedFailureOwner ??= this;
        lifetimeLease.Poison(failure);
        ProcessTeardownBarrier.Block(failure);
    }

    private void ReleaseFailedProcessOwner()
    {
        lock (ProcessOwnerGate)
        {
            if (ReferenceEquals(retainedFailureOwner, this)) retainedFailureOwner = null;
        }
    }

    private Task BeginDispose(out bool calledFromHookCallback)
    {
        lock (hookLifecycleGate)
        {
            calledFromHookCallback = callbackContexts.Value?.Depth > 0;
            if (hookDisposalCompletion is { } existing) return existing.Task;
            hookDisposalCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = hookDisposalCompletion.Task.ContinueWith(
                static completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            shutdownRequested = true;
        }
        Interlocked.Exchange(ref disposed, 1);
        IsAvailable = false;
        var completion = hookDisposalCompletion;
        lock (hookLifecycleGate)
        {
            deferredDispose ??= Task.Run(() => DisposeCoreAsync(completion!));
        }
        return completion!.Task;
    }

    public Task DisposeAsync()
    {
        return BeginDispose(out _);
    }

    public void Dispose()
    {
        var task = BeginDispose(out var calledFromHookCallback);
        if (!calledFromHookCallback) task.GetAwaiter().GetResult();
    }
}

public sealed record OfficialVoiceClipObservation(string ScdPath, uint SoundNumber, DateTimeOffset StartedAt);
