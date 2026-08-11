using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Bootstrap;

internal readonly record struct BaseHotLoadSafety(bool IsSafe, long Generation);

internal enum RuntimeFallbackOutcome
{
    Applied,
    SkippedUnsafe,
    Failed,
}

internal interface IRuntimeManagerTestSeam
{
    Exception? NativeOwnershipFailure { get; }

    bool BaseRuntimeReady => false;

    Task EnsureBaseRuntimeAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken token) => operation(token);

    void BaseRuntimeReleased() { }

    void BaseRuntimeDisposed() { }

    Task WaitForBaseOwnershipAsync(
        string operation,
        Func<Task> wait,
        CancellationToken token);

    void NativeOperationStarted(string operation);
}

public sealed class RuntimeManager : ITtsRuntime
{
    private static readonly TimeSpan UncertainReferenceOwnerGrace = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly BackendSelector selector = new();
    private readonly Configuration configuration;
    private readonly Action saveConfiguration;
    private readonly string talkerPath;
    private readonly string codecPath;
    private readonly string designModelHash;
    private readonly string runtimeVersion;
    private readonly string runtimePackDirectory;
    private readonly string modelDirectory;
    private readonly string referenceExtractorPath;
    private readonly string referenceExtractionDirectory;
    private readonly IReferenceExtractionProcessRunner referenceProcessRunner;
    private readonly bool useExternalBaseHost;
    private readonly Action<Exception>? nativeFailureReporter;
    private IProcessLifetimeLease? pluginLifetimeLease;
    private readonly object disposeGate = new();
    private readonly HashSet<string> unhealthy = [];
    private QwenCppRuntime? runtime;
    private IBaseRuntimeHost? baseHost;
    private Func<string, string, string, string, string, string, IBaseRuntimeHost>? baseHostFactory;
    private IRuntimeManagerTestSeam? testSeam;
    private readonly SemaphoreSlim baseOwnershipGate = new(1, 1);
    private readonly SemaphoreSlim referenceGate = new(1, 1);
    private readonly object baseHotLoadGate = new();
    private readonly CancellationTokenSource baseHotLoadShutdown = new();
    private readonly object referenceStateGate = new();
    private CancellationTokenSource? referenceCancellation;
    private Task? referenceTask;
    private Task? referenceFinalizationTask;
    private CancellationTokenSource? baseHotLoadCancellation;
    private Task? baseHotLoadTask;
    private Task? baseHotLoadObserverTask;
    private Task? baseHotLoadScheduleObserverTask;
    private Func<BaseHotLoadSafety>? baseHotLoadSafetyPredicate;
    private readonly SemaphoreSlim baseHotLoadTransitionGate = new(1, 1);
    private Task? baseHotLoadTransitionTask;
    private Task? baseHotLoadTransitionObserverTask;
    private int referenceActive;
    private int referenceProcessAbandoned;
    private int nativeFailureReported;
    private int baseHotLoadRescheduleSuppressed;
    private int switching;
    private int runtimeDisposalFailed;
    private int disposed;
    private Task? disposeTask;

    public string ModelHash { get; }
    public string BenchmarkIdentity { get; private set; } = string.Empty;
    public BackendSelection? Selection { get; private set; }
    private BackendSelection? lastStableSelection;
    public IReadOnlyList<BackendInfo> DetectedBackends { get; private set; } = [];
    public ITtsRuntime Runtime => this;
    public RuntimeCapabilities Capabilities
    {
        get
        {
            ThrowIfNativeOwnershipPoisoned();
            if (useExternalBaseHost)
                return new(true, true, false, true, DetectedBackends);
            var capabilities = runtime?.Capabilities
                ?? throw new InvalidOperationException("TTS runtime is not ready");
            return capabilities with { VoiceReferenceExtraction = true };
        }
    }
    public bool IsSwitching => Volatile.Read(ref switching) != 0;
    internal bool UsesExternalBaseHost => useExternalBaseHost;
    internal IProcessLifetimeLease PluginLifetimeLease => pluginLifetimeLease
        ?? throw new InvalidOperationException("Plugin process lifetime lease is not attached");
    public bool IsReady => (useExternalBaseHost
            ? Selection is { } selection
                && (baseHost is null
                    || IsBaseHostReadyForBackend(baseHost, selection.Effective.Name))
            : runtime is not null)
        && !IsSwitching && !HasNativeOwnershipFailure;
    public bool HasAbandonedReferenceProcess => Volatile.Read(ref referenceProcessAbandoned) != 0;
    public bool HasNativeOwnershipFailure =>
        Volatile.Read(ref referenceProcessAbandoned) != 0
        || Volatile.Read(ref nativeFailureReported) != 0
        || Volatile.Read(ref runtimeDisposalFailed) != 0
        || runtime?.HasTerminalDisposalFailure == true
        || QwenCppRuntime.HasNativeReleaseFailure
        || testSeam?.NativeOwnershipFailure is not null;
    public event Action<BackendSelection>? SelectionChanged;

    internal void SetTestSeam(IRuntimeManagerTestSeam seam)
    {
        ArgumentNullException.ThrowIfNull(seam);
        lock (disposeGate)
        {
            if (disposeTask is not null)
                throw new InvalidOperationException("Runtime manager test seam must be installed before disposal");
            testSeam = seam;
        }
    }

    internal void SetPluginLifetimeLease(IProcessLifetimeLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (disposeGate)
        {
            if (disposeTask is not null)
                throw new InvalidOperationException("Plugin lifetime lease must be attached before disposal");
            if (pluginLifetimeLease is not null && !ReferenceEquals(pluginLifetimeLease, lease))
                throw new InvalidOperationException("Plugin lifetime lease is already attached");
            pluginLifetimeLease = lease;
        }
    }

    internal void SetTestBackendState(BackendSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        DetectedBackends = [selection.Effective];
        Selection = selection;
    }

    internal void SetBaseHostFactory(
        Func<string, string, string, string, string, string, IBaseRuntimeHost> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (disposeGate)
        {
            if (disposeTask is not null)
                throw new InvalidOperationException("Base host factory must be installed before disposal");
            baseHostFactory = factory;
        }
    }

    internal void SetTestBaseHost(IBaseRuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (disposeGate)
        {
            if (disposeTask is not null)
                throw new InvalidOperationException("Base host must be installed before disposal");
            baseHost = host;
        }
    }

    internal void SetBaseHotLoadSafetyPredicate(Func<BaseHotLoadSafety> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (disposeGate)
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(RuntimeManager));
            Volatile.Write(ref baseHotLoadSafetyPredicate, predicate);
        }
    }

    internal Task SetBaseHotLoadEnabledAsync(bool enabled, CancellationToken token)
    {
        Task prior;
        Task task;
        lock (disposeGate)
        {
            if (Volatile.Read(ref disposed) != 0)
                return Task.FromException(new ObjectDisposedException(nameof(RuntimeManager)));
            configuration.KeepBaseModelLoaded = enabled;
            saveConfiguration();
        }
        lock (baseHotLoadGate)
        {
            if (Volatile.Read(ref disposed) != 0)
                return Task.FromException(new ObjectDisposedException(nameof(RuntimeManager)));
            prior = baseHotLoadTransitionTask ?? Task.CompletedTask;
            task = SetBaseHotLoadEnabledCoreAsync(enabled, token, prior);
            baseHotLoadTransitionTask = task;
            baseHotLoadTransitionObserverTask = ObserveBaseHotLoadTransitionAsync(task);
        }
        return task;
    }

    private async Task SetBaseHotLoadEnabledCoreAsync(
        bool enabled, CancellationToken token, Task prior)
    {
        try { await prior.ConfigureAwait(false); }
        catch { }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            token, baseHotLoadShutdown.Token);
        var transitionToken = enabled ? linked.Token : CancellationToken.None;
        await baseHotLoadTransitionGate.WaitAsync(transitionToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(RuntimeManager));
            if (!enabled)
            {
                await DisableBaseHotLoadAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
            await EnsureBaseHotLoadedWhenSafeAsync(linked.Token).ConfigureAwait(false);
        }
        finally { baseHotLoadTransitionGate.Release(); }
    }

    private async Task ObserveBaseHotLoadTransitionAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { _ = task.Exception; }
        finally { _ = Interlocked.CompareExchange(ref baseHotLoadTransitionTask, null, task); }
    }

    internal Task EnsureBaseHotLoadedWhenSafeAsync(CancellationToken token)
    {
        if (!configuration.KeepBaseModelLoaded) return Task.CompletedTask;
        if (Volatile.Read(ref disposed) != 0)
            return Task.FromException(new ObjectDisposedException(nameof(RuntimeManager)));
        ThrowIfNativeOwnershipPoisoned();
        var selectedBaseBackend = Selection?.Effective.Name;
        if ((useExternalBaseHost && selectedBaseBackend is not null
                && baseHost is { } residentHost
                && IsBaseHostReadyForBackend(residentHost, selectedBaseBackend))
            || (!useExternalBaseHost && runtime is not null)
            || testSeam?.BaseRuntimeReady == true)
            return Task.CompletedTask;

        BaseHotLoadSafety safety;
        var safetyPredicate = Volatile.Read(ref baseHotLoadSafetyPredicate);
        if (safetyPredicate is not null)
        {
            try { safety = safetyPredicate(); }
            catch { safety = new(false, 0); }
        }
        else
        {
            safety = new(true, 0);
        }
        if (!safety.IsSafe) return Task.CompletedTask;

        lock (baseHotLoadGate)
        {
            if (Volatile.Read(ref disposed) != 0)
                return Task.FromException(new ObjectDisposedException(nameof(RuntimeManager)));
            if (baseHotLoadTask is { IsCompleted: false }) return baseHotLoadTask;
            if (!IsBaseHotLoadStillSafe(safety.Generation)) return Task.CompletedTask;

            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                token, baseHotLoadShutdown.Token);
            var task = RestoreBaseHotLoadAsync(linked.Token, safety.Generation);
            baseHotLoadCancellation = linked;
            baseHotLoadTask = task;
            baseHotLoadObserverTask = ObserveBaseHotLoadAsync(task, linked);
            return task;
        }
    }

    internal void CancelBaseHotLoadRestore()
    {
        lock (baseHotLoadGate)
        {
            try { baseHotLoadCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task CancelAndAwaitBaseHotLoadRestoreAsync()
    {
        Task? active;
        lock (baseHotLoadGate)
        {
            try { baseHotLoadCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
            active = baseHotLoadTask;
        }
        if (active is null) return;
        if (Task.CurrentId == active.Id)
            throw new InvalidOperationException("Base hot-load cannot synthesize recursively");
        try { await active.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (
            Volatile.Read(ref disposed) != 0 || baseHotLoadShutdown.IsCancellationRequested) { }
    }

    private async Task DisableBaseHotLoadAsync(CancellationToken token)
    {
        CancelBaseHotLoadRestore();
        await CancelReferenceExtractionAsync().ConfigureAwait(false);
        Task? active;
        lock (baseHotLoadGate) active = baseHotLoadTask;
        if (active is not null)
        {
            try { await active.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        var enteredBaseOwnership = false;
        await WaitForBaseOwnershipAsync(nameof(DisableBaseHotLoadAsync), token).ConfigureAwait(false);
        enteredBaseOwnership = true;
        try
        {
            ThrowIfNativeOwnershipPoisoned();
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ThrowIfNativeOwnershipPoisoned();
                if (useExternalBaseHost)
                {
                    var previousHost = baseHost;
                    if (previousHost is null)
                    {
                        if (testSeam?.BaseRuntimeReady == true)
                            testSeam.BaseRuntimeDisposed();
                        return;
                    }
                    if (previousHost.IsBusy)
                        return;
                    Interlocked.Exchange(ref switching, 1);
                    try
                    {
                        NativeOperationStarted("base-host-disposal");
                        await previousHost.DisposeAsync().ConfigureAwait(false);
                        baseHost = null;
                        testSeam?.BaseRuntimeDisposed();
                    }
                    catch (Exception error)
                    {
                        Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                        ReportNativeFailure(error);
                        throw;
                    }
                    finally { Interlocked.Exchange(ref switching, 0); }
                    return;
                }
                var previous = runtime;
                if (previous is null)
                {
                    if (testSeam?.BaseRuntimeReady == true)
                        testSeam.BaseRuntimeDisposed();
                    return;
                }
                Interlocked.Exchange(ref switching, 1);
                try
                {
                    NativeOperationStarted("runtime-disposal");
                    await previous.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                    ReportNativeFailure(error);
                    throw;
                }
                runtime = null;
                testSeam?.BaseRuntimeDisposed();
            }
            finally { gate.Release(); }
        }
        finally
        {
            if (enteredBaseOwnership) baseOwnershipGate.Release();
            Interlocked.Exchange(ref switching, 0);
        }
    }

    private async Task RestoreBaseHotLoadAsync(CancellationToken token, long generation)
    {
        var seam = testSeam;
        if (seam is null)
            await EnsureReadyCoreAsync(token, generation, skipReferenceCancellation: true)
                .ConfigureAwait(false);
        else
        {
            await seam.EnsureBaseRuntimeAsync(
                async currentToken =>
                {
                    currentToken.ThrowIfCancellationRequested();
                    if (!IsBaseHotLoadStillSafe(generation)) return;
                    await EnsureReadyCoreAsync(
                        currentToken, generation, skipReferenceCancellation: true)
                        .ConfigureAwait(false);
                }, token)
                .ConfigureAwait(false);
            if (!IsBaseHotLoadStillSafe(generation) && seam.BaseRuntimeReady)
                seam.BaseRuntimeDisposed();
        }
    }

    private BaseHotLoadSafety ReadBaseHotLoadSafety()
    {
        var predicate = Volatile.Read(ref baseHotLoadSafetyPredicate);
        if (predicate is null) return new(true, 0);
        try { return predicate(); }
        catch { return new(false, 0); }
    }

    private bool IsBaseHotLoadStillSafe(long generation)
    {
        if (!configuration.KeepBaseModelLoaded
            || Volatile.Read(ref disposed) != 0
            || baseHotLoadShutdown.IsCancellationRequested
            || HasNativeOwnershipFailure) return false;
        var safety = ReadBaseHotLoadSafety();
        return safety.IsSafe && safety.Generation == generation;
    }

    private async Task ObserveBaseHotLoadAsync(
        Task task, CancellationTokenSource cancellation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (
            Volatile.Read(ref disposed) != 0 || baseHotLoadShutdown.IsCancellationRequested) { }
        catch
        {
            // The caller/owner observes the task result.  This continuation
            // observes it as well when a lifecycle callback cannot await it.
            _ = task.Exception;
            if (task.Exception?.GetBaseException() is { } error)
                ReportNativeFailure(error);
        }
        finally
        {
            Interlocked.CompareExchange(ref baseHotLoadCancellation, null, cancellation);
            cancellation.Dispose();
            if (Volatile.Read(ref disposed) == 0
                && !baseHotLoadShutdown.IsCancellationRequested
                && Volatile.Read(ref baseHotLoadRescheduleSuppressed) == 0
                && configuration.KeepBaseModelLoaded
                && !HasNativeOwnershipFailure
                && ReadBaseHotLoadSafety().IsSafe)
                ScheduleBaseHotLoadRestore();
        }
    }

    private void ScheduleBaseHotLoadRestore()
    {
        if (Volatile.Read(ref disposed) != 0
            || baseHotLoadShutdown.IsCancellationRequested
            || Volatile.Read(ref baseHotLoadRescheduleSuppressed) != 0
            || !configuration.KeepBaseModelLoaded
            || HasNativeOwnershipFailure)
            return;
        var safety = ReadBaseHotLoadSafety();
        if (!safety.IsSafe || !IsBaseHotLoadStillSafe(safety.Generation)) return;
        try
        {
            var restore = EnsureBaseHotLoadedWhenSafeAsync(CancellationToken.None);
            lock (baseHotLoadGate)
                baseHotLoadScheduleObserverTask = ObserveScheduledBaseHotLoadRestoreAsync(restore);
        }
        catch (ObjectDisposedException) when (
            Volatile.Read(ref disposed) != 0 || baseHotLoadShutdown.IsCancellationRequested) { }
        catch (Exception error) { ReportNativeFailure(error); }
    }

    private async Task ObserveScheduledBaseHotLoadRestoreAsync(Task restore)
    {
        try { await restore.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (
            Volatile.Read(ref disposed) != 0 || baseHotLoadShutdown.IsCancellationRequested) { }
        catch (Exception error) { ReportNativeFailure(error); }
    }

    private Task WaitForBaseOwnershipAsync(string operation, CancellationToken token)
    {
        var seam = testSeam;
        return seam is null
            ? baseOwnershipGate.WaitAsync(token)
            : seam.WaitForBaseOwnershipAsync(
                operation, () => baseOwnershipGate.WaitAsync(token), token);
    }

    private void NativeOperationStarted(string operation) => testSeam?.NativeOperationStarted(operation);

    public async Task EnsureReadyAsync(CancellationToken token)
        => await EnsureReadyCoreAsync(token, null).ConfigureAwait(false);

    private async Task EnsureReadyCoreAsync(
        CancellationToken token, long? hotLoadGeneration, bool skipReferenceCancellation = false)
    {
        if (!skipReferenceCancellation)
            await CancelReferenceExtractionAsync().ConfigureAwait(false);
        ThrowIfReferenceProcessAbandoned();
        if (hotLoadGeneration.HasValue
            && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value)) return;
        await WaitForBaseOwnershipAsync(nameof(EnsureReadyAsync), token).ConfigureAwait(false);
        try
        {
            ThrowIfNativeOwnershipPoisoned();
            if (hotLoadGeneration.HasValue
                && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value)) return;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ThrowIfNativeOwnershipPoisoned();
                if (Volatile.Read(ref disposed) != 0)
                    throw new ObjectDisposedException(nameof(RuntimeManager));
                if (hotLoadGeneration.HasValue
                    && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value)) return;
                if (useExternalBaseHost && !configuration.KeepBaseModelLoaded && baseHost is null)
                    return;
                if (runtime is null)
                {
                    var backendName = Selection?.Effective.Name
                        ?? throw new InvalidOperationException("TTS backend selection is not ready");
                    try
                    {
                        await EnsureRuntimeCoreAsync(backendName, token, hotLoadGeneration)
                            .ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        unhealthy.Add(backendName);
                        var fallback = await TryFallbackAsync(error, token, hotLoadGeneration)
                            .ConfigureAwait(false);
                        if (fallback is RuntimeFallbackOutcome.SkippedUnsafe) return;
                        if (fallback is RuntimeFallbackOutcome.Failed) throw;
                        SelectionChanged?.Invoke(Selection!);
                    }
                }
            }
            finally { gate.Release(); }
        }
        finally { baseOwnershipGate.Release(); }
    }

    public RuntimeManager(Configuration configuration, Action saveConfiguration, string talkerPath, string codecPath,
        string modelHash, string designModelHash, string runtimeVersion, string runtimePackDirectory,
        string referenceExtractorPath, string referenceExtractionDirectory,
        IReferenceExtractionProcessRunner? referenceProcessRunner = null, string? modelDirectory = null,
        Action<Exception>? nativeFailureReporter = null)
    {
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;
        this.talkerPath = talkerPath;
        this.codecPath = codecPath;
        ModelHash = modelHash;
        this.designModelHash = designModelHash;
        this.runtimeVersion = runtimeVersion;
        this.runtimePackDirectory = runtimePackDirectory;
        this.modelDirectory = Path.GetFullPath(modelDirectory ?? Path.GetDirectoryName(talkerPath)!);
        this.referenceExtractorPath = Path.GetFullPath(referenceExtractorPath);
        this.referenceExtractionDirectory = Path.GetFullPath(referenceExtractionDirectory);
        useExternalBaseHost = referenceProcessRunner is null;
        this.referenceProcessRunner = referenceProcessRunner ?? new ReferenceExtractionProcessRunner();
        this.nativeFailureReporter = nativeFailureReporter;
        CleanupReferenceExtractionDirectory();
    }

    public async Task InitializeAsync(CancellationToken token)
    {
        await CancelReferenceExtractionAsync().ConfigureAwait(false);
        ThrowIfReferenceProcessAbandoned();
        await WaitForBaseOwnershipAsync(nameof(InitializeAsync), token).ConfigureAwait(false);
        try
        {
            ThrowIfNativeOwnershipPoisoned();
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ThrowIfNativeOwnershipPoisoned();
                NativeOperationStarted("backend-enumeration");
                DetectedBackends = await Task.Run(() => QwenCppRuntime.EnumerateBackends(runtimePackDirectory), token).ConfigureAwait(false);
                BenchmarkIdentity = BackendBenchmark.Identity(runtimeVersion, ModelHash, designModelHash, configuration.Compute, DetectedBackends);
                var selection = selector.Select(configuration, DetectedBackends, BenchmarkIdentity);
                try
                {
                    if (!await ReplaceRuntime(selection.Effective.Name, token).ConfigureAwait(false))
                        return;
                    Selection = selection;
                    lastStableSelection = selection;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    unhealthy.Add(selection.Effective.Name);
                    Selection = selection;
                    var fallback = await TryFallbackAsync(error, token).ConfigureAwait(false);
                    if (fallback is RuntimeFallbackOutcome.SkippedUnsafe) return;
                    if (fallback is RuntimeFallbackOutcome.Failed) throw;
                }
                SelectionChanged?.Invoke(Selection!);
            }
            finally { gate.Release(); }
        }
        finally { baseOwnershipGate.Release(); }
    }

    public async Task SetDesiredAsync(BackendInfo backend, CancellationToken token)
    {
        if (!DetectedBackends.Any(candidate => candidate.Name == backend.Name))
            throw new ArgumentException("Device is not in the detected backend list", nameof(backend));

        await CancelReferenceExtractionAsync().ConfigureAwait(false);
        ThrowIfReferenceProcessAbandoned();
        await WaitForBaseOwnershipAsync(nameof(SetDesiredAsync), token).ConfigureAwait(false);
        try
        {
            ThrowIfNativeOwnershipPoisoned();
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ThrowIfNativeOwnershipPoisoned();
                // Explicit user action overrides any remembered unavailable device.
                BackendSelector.SetDesired(configuration, backend);
                saveConfiguration();
                unhealthy.Remove(backend.Name);
                var previousSelection = lastStableSelection ?? Selection;
                try
                {
                    if (!await ReplaceRuntime(backend.Name, token).ConfigureAwait(false))
                        return;
                    Selection = new(backend, backend, false, null);
                    lastStableSelection = Selection;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    unhealthy.Add(backend.Name);
                    Selection = previousSelection is { } stable
                        ? stable with
                        {
                            Desired = backend,
                            Error = $"Inference backend '{backend.Description}' could not be activated: {error.Message}"
                        }
                        : new(backend, backend, false,
                            $"Inference backend '{backend.Description}' could not be activated: {error.Message}");
                    var fallback = await TryFallbackAsync(error, token).ConfigureAwait(false);
                    if (fallback is RuntimeFallbackOutcome.SkippedUnsafe) return;
                    if (fallback is RuntimeFallbackOutcome.Failed) throw;
                }
                SelectionChanged?.Invoke(Selection);
            }
            finally { gate.Release(); }
        }
        finally { baseOwnershipGate.Release(); }
    }

    public async Task RefreshBackendsAsync(CancellationToken token)
    {
        await CancelReferenceExtractionAsync().ConfigureAwait(false);
        ThrowIfReferenceProcessAbandoned();
        await WaitForBaseOwnershipAsync(nameof(RefreshBackendsAsync), token).ConfigureAwait(false);
        try
        {
            ThrowIfNativeOwnershipPoisoned();
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ThrowIfNativeOwnershipPoisoned();
                NativeOperationStarted("backend-enumeration");
                IReadOnlyList<BackendInfo> detected;
                if (useExternalBaseHost && baseHost?.IsReady == true && DetectedBackends.Count > 0)
                    detected = DetectedBackends;
                else
                    detected = await Task.Run(
                        () => QwenCppRuntime.EnumerateBackends(runtimePackDirectory), token)
                        .ConfigureAwait(false);
                var identity = BackendBenchmark.Identity(
                    runtimeVersion, ModelHash, designModelHash, configuration.Compute, detected);
                var selection = selector.Select(configuration, detected, identity);
                DetectedBackends = detected;
                BenchmarkIdentity = identity;
                if (runtime is null || Selection?.Effective.Name != selection.Effective.Name)
                {
                    var previousSelection = lastStableSelection ?? Selection;
                    try
                    {
                        if (!await ReplaceRuntime(selection.Effective.Name, token).ConfigureAwait(false))
                            return;
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        unhealthy.Add(selection.Effective.Name);
                        Selection = previousSelection is { } stable
                            ? stable with
                            {
                                Desired = selection.Desired,
                                Error = $"Inference backend '{selection.Effective.Description}' could not be activated: {error.Message}"
                            }
                            : selection with
                            {
                                Error = $"Inference backend '{selection.Effective.Description}' could not be activated: {error.Message}"
                            };
                        var fallback = await TryFallbackAsync(error, token).ConfigureAwait(false);
                        if (fallback is RuntimeFallbackOutcome.SkippedUnsafe) return;
                        if (fallback is RuntimeFallbackOutcome.Failed) throw;
                        SelectionChanged?.Invoke(Selection!);
                        return;
                    }
                }
                Selection = selection;
                lastStableSelection = selection;
                SelectionChanged?.Invoke(selection);
            }
            finally { gate.Release(); }
        }
        finally { baseOwnershipGate.Release(); }
    }

    private async Task<bool> ReplaceRuntime(
        string backendName, CancellationToken token, long? hotLoadGeneration = null)
    {
        ThrowIfNativeOwnershipPoisoned();
        if (hotLoadGeneration.HasValue
            && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value))
            return false;
        if (useExternalBaseHost)
        {
            if (!configuration.KeepBaseModelLoaded && baseHost is null)
                return true;
            IBaseRuntimeHost? activeHost = baseHost;
            Interlocked.Exchange(ref switching, 1);
            try
            {
                ThrowIfNativeOwnershipPoisoned();
                if (hotLoadGeneration.HasValue
                    && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value)) return false;
                var host = activeHost;
                if (host is null || !host.IsReady)
                {
                    if (host is not null)
                    {
                        try { await host.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception error) when (error is BaseRuntimeHostException
                                                       { ProcessMayBeRunning: true })
                        {
                            Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                            ReportNativeFailure(error);
                            throw;
                        }
                        baseHost = null;
                    }
                    host ??= CreateBaseHost();
                    activeHost = host;
                    NativeOperationStarted("base-host-start");
                    await host.StartAsync(backendName, token).ConfigureAwait(false);
                    if (!IsBaseHostReadyForBackend(host, backendName))
                        throw new BaseRuntimeHostException(
                            "Base runtime host did not confirm the requested backend context");
                    if (hotLoadGeneration.HasValue
                        && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value))
                    {
                        await host.DisposeAsync().ConfigureAwait(false);
                        return false;
                    }
                    baseHost = host;
                }
                else
                {
                    NativeOperationStarted("base-host-backend-switch");
                    await host.SwitchBackendAsync(backendName, token).ConfigureAwait(false);
                    if (!IsBaseHostReadyForBackend(host, backendName))
                        throw new BaseRuntimeHostException(
                            "Base runtime host did not confirm the requested backend context");
                }
                Interlocked.Exchange(ref runtimeDisposalFailed, 0);
                return true;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (error is BaseRuntimeHostException { ProcessMayBeRunning: true })
                {
                    if (activeHost is not null) baseHost = activeHost;
                    Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                    ReportNativeFailure(error);
                }
                else if (activeHost is not null && !ReferenceEquals(baseHost, activeHost))
                {
                    try { await activeHost.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanupError)
                    {
                        Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                        ReportNativeFailure(cleanupError);
                    }
                }
                throw;
            }
            finally { Interlocked.Exchange(ref switching, 0); }
        }
        Interlocked.Exchange(ref switching, 1);
        try
        {
            ThrowIfNativeOwnershipPoisoned();
            if (hotLoadGeneration.HasValue
                && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value))
                return false;
            var previous = runtime;
            if (previous is not null)
            {
                try { await previous.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error)
                {
                    Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                    ReportNativeFailure(error);
                    throw;
                }
            }
            // Keep the old reference published until disposal succeeds.  A
            // disposal failure must leave a coherent failed state rather than
            // allowing a replacement context to overlap an unresolved native
            // lease.
            if (previous is not null) runtime = null;
            try
            {
                ThrowIfNativeOwnershipPoisoned();
                if (hotLoadGeneration.HasValue
                    && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value))
                    return false;
                NativeOperationStarted("runtime-construction");
                var replacement = await Task.Run(
                        () => new QwenCppRuntime(talkerPath, codecPath, backendName), token)
                    .ConfigureAwait(false);
                if (hotLoadGeneration.HasValue
                    && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value))
                {
                    await DisposeUnpublishedRuntimeAsync(replacement).ConfigureAwait(false);
                    return false;
                }
                if (hotLoadGeneration.HasValue)
                {
                    if (!TryPublishHotLoadRuntime(replacement, hotLoadGeneration.Value))
                    {
                        await DisposeUnpublishedRuntimeAsync(replacement).ConfigureAwait(false);
                        return false;
                    }
                }
                else
                {
                    runtime = replacement;
                }
                Interlocked.Exchange(ref runtimeDisposalFailed, 0);
                return true;
            }
            catch
            {
                runtime = null;
                throw;
            }
        }
        finally { Interlocked.Exchange(ref switching, 0); }
    }

    public async Task BenchmarkAndApplyAsync(string designPath, CancellationToken token)
    {
        if (configuration.Compute == ComputePreference.Manual) return;
        await CancelReferenceExtractionAsync().ConfigureAwait(false);
        ThrowIfReferenceProcessAbandoned();
        await WaitForBaseOwnershipAsync(nameof(BenchmarkAndApplyAsync), token).ConfigureAwait(false);
        try
        {
            ThrowIfNativeOwnershipPoisoned();
            if (useExternalBaseHost)
            {
                var benchmarkBackend = Selection?.Effective.Name
                    ?? throw new InvalidOperationException("TTS backend selection is not ready");
                if (baseHost is null || !IsBaseHostReadyForBackend(baseHost, benchmarkBackend))
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await EnsureRuntimeCoreAsync(benchmarkBackend, token).ConfigureAwait(false);
                    }
                    finally { gate.Release(); }
                }
                var host = baseHost ?? throw new InvalidOperationException("Base runtime host is not ready");
                if (!IsBaseHostReadyForBackend(host, benchmarkBackend))
                    throw new BaseRuntimeHostException(
                        "Base runtime host is unavailable for the requested benchmark context");
                var cachedHost = configuration.BackendBenchmark;
                if (cachedHost is null || cachedHost.Identity != BenchmarkIdentity)
                {
                    NativeOperationStarted("base-host-benchmark");
                    var measurements = await host.BenchmarkAsync(DetectedBackends, token)
                        .ConfigureAwait(false);
                    // Keep the existing VoiceDesign benchmark path for the
                    // in-process design model. Base measurements above remain
                    // authoritative for Base-host device selection.
                    try
                    {
                        _ = await BackendBenchmark.RunAsync(BenchmarkIdentity, designPath, codecPath,
                            DetectedBackends, configuration.Compute, token,
                            PluginLifetimeLease).ConfigureAwait(false);
                    }
                    catch (BackendBenchmarkCleanupException error)
                    {
                        ReportNativeFailure(error);
                        throw;
                    }
                    var winnerHost = BackendBenchmark.SelectWinner(
                        DetectedBackends, measurements, configuration.Compute)
                        ?? throw new InvalidOperationException("No Base runtime host backend passed the benchmark");
                    cachedHost = new BackendBenchmarkCache(
                        BenchmarkIdentity, winnerHost.Name, DateTimeOffset.UtcNow, measurements);
                    configuration.BackendBenchmark = cachedHost;
                    saveConfiguration();
                }
                var hostWinner = DetectedBackends.FirstOrDefault(value => value.Name == cachedHost.WinnerName)
                    ?? throw new InvalidOperationException("Cached Base runtime host benchmark device is unavailable");
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    ThrowIfNativeOwnershipPoisoned();
                    if (!host.ContextReady
                        || !String.Equals(host.ActiveBackendId, hostWinner.Name,
                            StringComparison.Ordinal))
                    {
                        await host.SwitchBackendAsync(hostWinner.Name, token).ConfigureAwait(false);
                        if (!host.ContextReady
                            || !String.Equals(host.ActiveBackendId, hostWinner.Name,
                                StringComparison.Ordinal))
                            throw new InvalidOperationException(
                                "Base runtime host did not confirm the benchmark winner backend");
                    }
                    if (Selection?.Effective.Name != hostWinner.Name)
                    {
                        if (!await ReplaceRuntime(hostWinner.Name, token).ConfigureAwait(false)) return;
                        Selection = new(hostWinner, hostWinner, false, null);
                        lastStableSelection = Selection;
                        SelectionChanged?.Invoke(Selection);
                    }
                }
                finally { gate.Release(); }
                if (!configuration.KeepBaseModelLoaded && ReferenceEquals(baseHost, host))
                {
                    await host.DisposeAsync().ConfigureAwait(false);
                    baseHost = null;
                }
                return;
            }
            var cached = configuration.BackendBenchmark;
            if (cached is null || cached.Identity != BenchmarkIdentity)
            {
                try
                {
                    NativeOperationStarted("backend-benchmark");
                    cached = await BackendBenchmark.RunAsync(BenchmarkIdentity, designPath, codecPath,
                        DetectedBackends, configuration.Compute, token,
                        PluginLifetimeLease).ConfigureAwait(false);
                }
                catch (BackendBenchmarkCleanupException error)
                {
                    ReportNativeFailure(error);
                    throw;
                }
                configuration.BackendBenchmark = cached;
                saveConfiguration();
            }
            var winner = DetectedBackends.FirstOrDefault(candidate => candidate.Name == cached.WinnerName);
            if (winner is null || configuration.Compute == ComputePreference.Manual) return;

            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ThrowIfNativeOwnershipPoisoned();
                if (runtime is not null && Selection?.Effective.Name == winner.Name) return;
                unhealthy.Remove(winner.Name);
                var previousSelection = lastStableSelection ?? Selection;
                try
                {
                    if (!await ReplaceRuntime(winner.Name, token).ConfigureAwait(false))
                        return;
                    Selection = new(winner, winner, false, null);
                    lastStableSelection = Selection;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    unhealthy.Add(winner.Name);
                    Selection = previousSelection is { } stable
                        ? stable with
                        {
                            Desired = winner,
                            Error = $"Benchmark winner '{winner.Description}' could not be activated: {error.Message}"
                        }
                        : new(winner, winner, false,
                            $"Benchmark winner '{winner.Description}' could not be activated: {error.Message}");
                    var fallback = await TryFallbackAsync(error, token).ConfigureAwait(false);
                    if (fallback is RuntimeFallbackOutcome.SkippedUnsafe) return;
                    if (fallback is RuntimeFallbackOutcome.Failed) throw;
                }
                SelectionChanged?.Invoke(Selection!);
            }
            finally { gate.Release(); }
        }
        finally
        {
            baseOwnershipGate.Release();
        }
    }

    public async ValueTask<VoiceReference> ExtractReferenceAsync(
        ReadOnlyMemory<float> monoPcm24Khz,
        string transcript,
        CancellationToken token)
    {
        if (Volatile.Read(ref disposed) != 0)
            throw new ObjectDisposedException(nameof(RuntimeManager));
        ThrowIfReferenceProcessAbandoned();
        ValidateReferenceInput(monoPcm24Khz, transcript);
        token.ThrowIfCancellationRequested();
        if (useExternalBaseHost)
        {
            var expectedBackend = Selection?.Effective.Name
                ?? throw new InvalidOperationException("TTS backend selection is not ready");
            await WaitForBaseOwnershipAsync(nameof(ExtractReferenceAsync), token).ConfigureAwait(false);
            try
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    ThrowIfNativeOwnershipPoisoned();
                    await EnsureRuntimeCoreAsync(expectedBackend, token).ConfigureAwait(false);
                }
                finally { gate.Release(); }
            }
            finally { baseOwnershipGate.Release(); }

            var host = baseHost
                ?? throw new InvalidOperationException("Base runtime host is not ready");
            if (!IsBaseHostReadyForBackend(host, expectedBackend))
                throw new BaseRuntimeHostException(
                    "Base runtime host is unavailable for the requested backend context");
            var reference = await host.ExtractReferenceAsync(monoPcm24Khz, transcript, token)
                .ConfigureAwait(false);
            if (!configuration.KeepBaseModelLoaded && !host.IsBusy)
            {
                await WaitForBaseOwnershipAsync(nameof(ExtractReferenceAsync), CancellationToken.None)
                    .ConfigureAwait(false);
                try
                {
                    await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        if (ReferenceEquals(baseHost, host))
                        {
                            await host.DisposeAsync().ConfigureAwait(false);
                            baseHost = null;
                        }
                    }
                    finally { gate.Release(); }
                }
                finally { baseOwnershipGate.Release(); }
            }
            return reference;
        }
        var helper = ResolveReferenceExtractorPath();
        Directory.CreateDirectory(referenceExtractionDirectory);
        ValidateReferenceExtractionRoot();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var completion = new TaskCompletionSource<VoiceReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (referenceStateGate)
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(RuntimeManager));
            if (referenceActive != 0)
                throw new InvalidOperationException("Reference extraction is already active");
            referenceActive = 1;
            referenceCancellation = linked;
            referenceTask = completion.Task;
            referenceFinalizationTask = completion.Task;
        }

        var enteredBaseOwnership = false;
        var enteredReferenceGate = false;
        VoiceReference? result = null;
        ExceptionDispatchInfo? failure = null;
        var scheduleRestore = false;
        try
        {
            await WaitForBaseOwnershipAsync(nameof(ExtractReferenceAsync), linked.Token).ConfigureAwait(false);
            enteredBaseOwnership = true;
            ThrowIfNativeOwnershipPoisoned();
            await referenceGate.WaitAsync(linked.Token).ConfigureAwait(false);
            enteredReferenceGate = true;
            ThrowIfNativeOwnershipPoisoned();
            var backendName = await ReleaseLiveBaseRuntimeAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            NativeOperationStarted("reference-extraction");
            result = await RunReferenceExtractorAsync(helper, backendName, monoPcm24Khz, transcript, linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (error is ReferenceExtractionProcessException { ProcessMayBeRunning: true })
                MarkReferenceProcessAbandoned(error);
            failure = ExceptionDispatchInfo.Capture(error);
        }
        finally
        {
            if (enteredReferenceGate) referenceGate.Release();
            Interlocked.Exchange(ref switching, 0);
            var shouldRestore = configuration.KeepBaseModelLoaded
                                && Volatile.Read(ref disposed) == 0
                                && !HasNativeOwnershipFailure;
            lock (referenceStateGate)
            {
                if (ReferenceEquals(referenceCancellation, linked))
                {
                    referenceCancellation = null;
                    referenceTask = null;
                    referenceActive = 0;
                }
                if (failure is null && result is null)
                    failure = ExceptionDispatchInfo.Capture(
                        new InvalidOperationException("Reference extraction completed without a reference"));
                if (failure is not null)
                    completion.TrySetException(failure.SourceException);
                else
                    completion.TrySetResult(result!);
                scheduleRestore = shouldRestore;
                if (ReferenceEquals(referenceFinalizationTask, completion.Task))
                    referenceFinalizationTask = null;
            }
            // Publish reference completion and clear extraction state before
            // scheduling the independent resident-Base restore. Release the
            // extraction ownership first so restore never waits on a gate
            // still held by this finalizer, and cannot re-enter extraction
            // cancellation.
            if (enteredBaseOwnership) baseOwnershipGate.Release();
            if (scheduleRestore) ScheduleBaseHotLoadRestore();
        }

        if (failure is not null)
        {
            failure.Throw();
        }
        return result!;
    }

    private async Task<string> ReleaseLiveBaseRuntimeAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfNativeOwnershipPoisoned();
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(RuntimeManager));
            if (Volatile.Read(ref runtimeDisposalFailed) != 0)
                throw new InvalidOperationException(
                    "The previous inference runtime did not dispose safely; restart is required before reference extraction");
            var backendName = Selection?.Effective.Name
                ?? throw new InvalidOperationException("TTS backend selection is not ready");
            testSeam?.BaseRuntimeReleased();
            // This manager owns only the Base model. VoiceDesigner owns the
            // separate VoiceDesign model and may remain active while the
            // helper exclusively owns Base extraction.
            Interlocked.Exchange(ref switching, 1);
            var previous = runtime;
            if (previous is not null)
            {
                NativeOperationStarted("runtime-disposal");
                try { await previous.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error)
                {
                    Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                    ReportNativeFailure(error);
                    throw;
                }
            }
            runtime = null;
            return backendName;
        }
        finally { gate.Release(); }
    }

    private async Task<VoiceReference> RunReferenceExtractorAsync(
        string helper, string backendName, ReadOnlyMemory<float> monoPcm24Khz, string transcript,
        CancellationToken token)
    {
        Directory.CreateDirectory(referenceExtractionDirectory);
        CleanupReferenceExtractionDirectory();
        var id = Guid.NewGuid().ToString("N");
        var runDirectory = Path.Combine(referenceExtractionDirectory, id);
        Directory.CreateDirectory(runDirectory);
        var inputPath = Path.Combine(runDirectory, "input.f32");
        var requestPath = Path.Combine(runDirectory, "request.json");
        var outputPath = Path.Combine(runDirectory, "response.json");
        var ownershipPath = Path.Combine(runDirectory, "owner.json");
        var ownershipPendingPath = ownershipPath + ".pending";
        var launchPermitPath = Path.Combine(runDirectory, "launch.ready");
        var inputTemporary = inputPath + ".part";
        var requestTemporary = requestPath + ".part";
        var requestNonce = Guid.NewGuid().ToString("N");
        var preserveFilesForAbandonedProcess = false;
        try
        {
            ReferenceExtractionProtocol.ValidateTransientPath(inputTemporary,
                runDirectory, "input PCM temporary path");
            var bytes = new byte[checked(monoPcm24Khz.Length * sizeof(float))];
            Buffer.BlockCopy(monoPcm24Khz.ToArray(), 0, bytes, 0, bytes.Length);
            await File.WriteAllBytesAsync(inputTemporary, bytes, token).ConfigureAwait(false);
            File.Move(inputTemporary, inputPath, true);
            var request = new ReferenceExtractionRequest(
                ReferenceExtractionProtocol.SchemaVersion,
                ReferenceExtractionProtocol.AbiVersion,
                Path.GetFullPath(runtimePackDirectory),
                Path.GetFullPath(talkerPath),
                Path.GetFullPath(codecPath),
                backendName,
                Path.GetFullPath(inputPath),
                Path.GetFullPath(outputPath),
                transcript,
                Path.GetFullPath(runtimePackDirectory),
                modelDirectory,
                Path.GetFullPath(runDirectory),
                requestNonce,
                Path.GetDirectoryName(helper)!);
            ReferenceExtractionProtocol.ValidateRequest(request, requireInput: true);
            ReferenceExtractionProtocol.ValidateTransientPath(requestPath,
                runDirectory, "request path");
            ReferenceExtractionProtocol.ValidateTransientPath(requestTemporary,
                runDirectory, "request temporary path");
            ReferenceExtractionProtocol.ValidateTransientPath(ownershipPath,
                runDirectory, "ownership path");
            ReferenceExtractionProtocol.ValidateTransientPath(ownershipPendingPath,
                runDirectory, "ownership pending path");
            ReferenceExtractionProtocol.ValidateTransientPath(ownershipPendingPath + ".part",
                runDirectory, "ownership pending temporary path");
            var requestJson = JsonSerializer.Serialize(request, ReferenceExtractionProtocol.JsonOptions());
            await File.WriteAllTextAsync(requestTemporary, requestJson, token).ConfigureAwait(false);
            File.Move(requestTemporary, requestPath, true);
            // Publish an uncertainty marker before starting the helper.  If the
            // parent dies before the helper can publish its PID/start identity,
            // startup cleanup must retain the run rather than guessing that it
            // is safe to delete a possibly live native owner.
            WritePendingOwnership(ownershipPendingPath, requestNonce);
            var process = referenceProcessRunner is IReferenceExtractionOwnershipRunner ownershipRunner
                ? await ownershipRunner.RunAsync(helper, requestPath, ownershipPath,
                    Path.GetDirectoryName(helper)!, requestNonce, token).ConfigureAwait(false)
                : await referenceProcessRunner.RunAsync(helper, requestPath, token).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new ReferenceExtractionProcessException(process.ExitCode, process.StandardError);
            if (!File.Exists(outputPath))
                throw new ReferenceExtractionProcessException(6, "helper exited without an output response");
            if (new FileInfo(outputPath).Length > ReferenceExtractionProtocol.MaximumResponseBytes)
                throw new ReferenceExtractionProcessException(6, "helper response is too large");
            var responseJson = await File.ReadAllTextAsync(outputPath, token).ConfigureAwait(false);
            var response = ReferenceExtractionProtocol.ParseResponse(responseJson, transcript);
            return new VoiceReference(response.SpeakerEmbedding, response.RvqCodes,
                response.ReferenceLength, response.Codebooks, response.Transcript);
        }
        catch (ReferenceExtractionProcessException error) when (error.ProcessMayBeRunning)
        {
            preserveFilesForAbandonedProcess = true;
            throw;
        }
        finally
        {
            if (!preserveFilesForAbandonedProcess)
            {
                DeleteReferenceFile(inputTemporary);
                DeleteReferenceFile(inputPath);
                DeleteReferenceFile(requestTemporary);
                DeleteReferenceFile(requestPath);
                DeleteReferenceFile(ownershipPath + ".part");
                DeleteReferenceFile(ownershipPath);
                DeleteReferenceFile(ownershipPendingPath + ".part");
                DeleteReferenceFile(ownershipPendingPath);
                DeleteReferenceFile(launchPermitPath + ".part");
                DeleteReferenceFile(launchPermitPath);
                DeleteReferenceFile(outputPath + ".part");
                DeleteReferenceFile(outputPath);
                TryDeleteReferenceDirectory(runDirectory);
            }
        }
    }

    private static void ValidateReferenceInput(ReadOnlyMemory<float> monoPcm24Khz, string transcript)
    {
        if (monoPcm24Khz.Length == 0 || monoPcm24Khz.Length > ReferenceExtractionProtocol.MaximumSamples)
            throw new InvalidDataException("Reference extraction PCM is invalid");
        foreach (var value in monoPcm24Khz.Span)
            if (!float.IsFinite(value)) throw new InvalidDataException("Reference extraction PCM is invalid");
        if (String.IsNullOrWhiteSpace(transcript)
            || transcript.Length > ReferenceExtractionProtocol.MaximumTranscriptCharacters)
            throw new InvalidDataException("Reference extraction transcript is invalid");
    }

    private string ResolveReferenceExtractorPath()
    {
        if (File.Exists(referenceExtractorPath)) return Path.GetFullPath(referenceExtractorPath);
        var alternate = referenceExtractorPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? referenceExtractorPath[..^4]
            : referenceExtractorPath + ".exe";
        if (File.Exists(alternate)) return Path.GetFullPath(alternate);
        throw new FileNotFoundException("Reference extraction helper is not installed", referenceExtractorPath);
    }

    private static void DeleteReferenceFile(string path)
    {
        if (!IsSafeRegularFile(path)) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void WritePendingOwnership(string path, string requestNonce)
    {
        var temporary = path + ".part";
        using var process = Process.GetCurrentProcess();
        var owner = new ReferenceExtractionOwnership(
            process.Id, process.StartTime.ToUniversalTime().Ticks, requestNonce);
        File.WriteAllText(temporary, JsonSerializer.Serialize(owner));
        File.Move(temporary, path, true);
    }

    private void TryDeleteReferenceDirectory(string path)
    {
        var root = Path.GetFullPath(referenceExtractionDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = root + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || String.Equals(directory, root, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            ReferenceExtractionProtocol.ValidateTransientPath(
                Path.Combine(directory, ".cleanup-probe"), directory, "run cleanup path");
            foreach (var ownerPath in EnumerateSafeFiles(directory, "owner.json"))
            {
                try
                {
                    var owner = JsonSerializer.Deserialize<ReferenceExtractionOwnership>(
                        File.ReadAllText(ownerPath));
                    if (owner is null || owner.ProcessId <= 0 || owner.ProcessStartUtcTicks <= 0
                    || String.IsNullOrWhiteSpace(owner.RequestNonce)
                        || IsVerifiedOwnerAlive(owner)) return;
                }
                catch (IOException) { return; }
                catch (UnauthorizedAccessException) { return; }
                catch (JsonException) { return; }
            }
            if (!TryDeleteSafeDirectoryTree(directory)) return;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
        catch (JsonException) { }
    }

    private void CleanupReferenceExtractionDirectory()
    {
        try
        {
            Directory.CreateDirectory(referenceExtractionDirectory);
            ValidateReferenceExtractionRoot();
            using var cleanupLease = TryAcquireReferenceCleanupLease();
            if (cleanupLease is null) return;
            foreach (var pendingPath in EnumerateSafeFiles(referenceExtractionDirectory, "owner.json.pending").ToArray())
            {
                if (!TryReapUncertainReferenceOwner(pendingPath)) continue;
                var pendingDirectory = Path.GetDirectoryName(pendingPath);
                if (pendingDirectory is not null) TryDeleteReferenceDirectory(pendingDirectory);
            }
            var owners = EnumerateSafeFiles(referenceExtractionDirectory, "owner.json")
                .Concat(Directory.EnumerateFiles(referenceExtractionDirectory, "*.owner.json",
                    SearchOption.TopDirectoryOnly).Where(IsSafeRegularFile))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var ownerPath in owners)
            {
                ReferenceExtractionOwnership? owner;
                try
                {
                    ReferenceExtractionProtocol.ValidateTransientPath(
                        ownerPath, referenceExtractionDirectory, "ownership metadata");
                    owner = JsonSerializer.Deserialize<ReferenceExtractionOwnership>(
                        File.ReadAllText(ownerPath));
                    if (owner is null || owner.ProcessId <= 0 || owner.ProcessStartUtcTicks <= 0
                        || String.IsNullOrWhiteSpace(owner.RequestNonce)) continue;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                               or JsonException or NotSupportedException or InvalidDataException)
                {
                    // An incomplete owner record cannot be safely attributed.
                    continue;
                }

                if (!OwnershipMatchesRequest(ownerPath, owner)) continue;
                if (IsVerifiedOwnerAlive(owner)) continue;
                var runDirectory = Path.GetDirectoryName(ownerPath);
                if (runDirectory is not null
                    && !String.Equals(Path.GetFullPath(runDirectory),
                        Path.GetFullPath(referenceExtractionDirectory), StringComparison.OrdinalIgnoreCase))
                    TryDeleteReferenceDirectory(runDirectory);
                else
                {
                    // Retain compatibility with pre-run-directory records;
                    // only the verified dead owner's exact nonce prefix is
                    // eligible for cleanup.
                    var id = Path.GetFileName(ownerPath)[..^".owner.json".Length];
                    foreach (var path in Directory.EnumerateFiles(referenceExtractionDirectory,
                                 id + ".*", SearchOption.TopDirectoryOnly))
                        DeleteReferenceFile(path);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
    }

    private FileStream? TryAcquireReferenceCleanupLease()
    {
        var root = Path.GetFullPath(referenceExtractionDirectory);
        var leasePath = Path.Combine(root, ".cleanup.lock");
        try
        {
            if (File.Exists(leasePath)
                && File.GetAttributes(leasePath).HasFlag(FileAttributes.ReparsePoint)) return null;
            return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.SequentialScan);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool TryReapUncertainReferenceOwner(string ownerPath)
    {
        try
        {
            if (!IsSafeRegularFile(ownerPath)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(ownerPath) < UncertainReferenceOwnerGrace)
                return false;
            if (!IsSafeRegularFile(ownerPath)) return false;
            var owner = JsonSerializer.Deserialize<ReferenceExtractionOwnership>(
                File.ReadAllText(ownerPath));
            if (owner is null || owner.ProcessId <= 0 || owner.ProcessStartUtcTicks <= 0
                || String.IsNullOrWhiteSpace(owner.RequestNonce) || IsVerifiedOwnerAlive(owner)) return false;
            if (!IsSafeRegularFile(ownerPath)) return false;
            File.Delete(ownerPath);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                      or JsonException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root, string fileName)
    {
        var pending = new Stack<string>([Path.GetFullPath(root)]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (!IsSafeDirectory(directory)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory, fileName, SearchOption.TopDirectoryOnly); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var path in files)
                if (IsSafeRegularFile(path)) yield return path;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var child in children)
                if (IsSafeDirectory(child)) pending.Push(child);
        }
    }

    private static bool TryDeleteSafeDirectoryTree(string root)
    {
        var pending = new Stack<string>([Path.GetFullPath(root)]);
        var directories = new List<string>();
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (!IsSafeDirectory(directory)) return false;
            directories.Add(directory);
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                    if (!IsSafeRegularFile(file)) return false;
                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsSafeDirectory(child)) return false;
                    pending.Push(child);
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        foreach (var directory in directories.OrderByDescending(value => value.Length))
        {
            if (!IsSafeDirectory(directory)) return false;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsSafeRegularFile(file)) return false;
                    if (String.Equals(Path.GetFileName(file), "owner.json", StringComparison.OrdinalIgnoreCase))
                    {
                        var owner = JsonSerializer.Deserialize<ReferenceExtractionOwnership>(
                            File.ReadAllText(file));
                        if (owner is null || owner.ProcessId <= 0 || owner.ProcessStartUtcTicks <= 0
                            || String.IsNullOrWhiteSpace(owner.RequestNonce)
                            || IsVerifiedOwnerAlive(owner)) return false;
                    }
                    if (String.Equals(Path.GetFileName(file), "owner.json.pending", StringComparison.OrdinalIgnoreCase))
                    {
                        var owner = JsonSerializer.Deserialize<ReferenceExtractionOwnership>(
                            File.ReadAllText(file));
                        if (owner is null || owner.ProcessId <= 0 || owner.ProcessStartUtcTicks <= 0
                            || String.IsNullOrWhiteSpace(owner.RequestNonce)
                            || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < UncertainReferenceOwnerGrace
                            || IsVerifiedOwnerAlive(owner)) return false;
                    }
                    File.Delete(file);
                }
                if (Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly).Any())
                    return false;
                Directory.Delete(directory, recursive: false);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        return true;
    }

    private static bool IsSafeDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory)
                && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsSafeRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Directory)
                && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void ValidateReferenceExtractionRoot()
    {
        var root = Path.GetFullPath(referenceExtractionDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Reference extraction root is missing: {root}");
        ReferenceExtractionProtocol.ValidateTransientPath(
            Path.Combine(root, ".root-probe"), root, "reference extraction root");
    }

    private static bool IsVerifiedOwnerAlive(ReferenceExtractionOwnership owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            var startTicks = process.StartTime.ToUniversalTime().Ticks;
            return startTicks == owner.ProcessStartUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private bool OwnershipMatchesRequest(string ownerPath, ReferenceExtractionOwnership owner)
    {
        var runDirectory = Path.GetDirectoryName(ownerPath);
        if (runDirectory is null) return false;
        var requestPath = String.Equals(Path.GetFileName(ownerPath), "owner.json",
                StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(runDirectory, "request.json")
            : Path.Combine(referenceExtractionDirectory,
                Path.GetFileName(ownerPath)[..^".owner.json".Length] + ".request.json");
        try
        {
            if (!File.Exists(requestPath)) return false;
            ReferenceExtractionProtocol.ValidateTransientPath(
                requestPath, runDirectory, "request metadata");
            var json = File.ReadAllText(requestPath);
            try
            {
                var request = JsonSerializer.Deserialize<ReferenceExtractionRequest>(
                    json, ReferenceExtractionProtocol.JsonOptions());
                if (request is not null
                    && String.Equals(request.RequestNonce, owner.RequestNonce, StringComparison.Ordinal))
                    return true;
            }
            catch (JsonException) { }

            var hostRequest = JsonSerializer.Deserialize<BaseHostLaunchRequest>(
                json, BaseHostProtocol.JsonOptions());
            return hostRequest is not null
                   && String.Equals(hostRequest.RequestNonce, owner.RequestNonce, StringComparison.Ordinal)
                   && String.Equals(Path.GetFullPath(hostRequest.TrustedHostRoot),
                       Path.GetFullPath(runDirectory),
                       OperatingSystem.IsWindows()
                           ? StringComparison.OrdinalIgnoreCase
                           : StringComparison.Ordinal)
                   && String.Equals(Path.GetFullPath(hostRequest.TrustedReferenceRoot),
                       Path.GetFullPath(referenceExtractionDirectory),
                       OperatingSystem.IsWindows()
                           ? StringComparison.OrdinalIgnoreCase
                           : StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                       or JsonException or NotSupportedException or InvalidDataException
                                       or ArgumentException)
        {
            return false;
        }
    }

    private async Task CancelReferenceExtractionAsync(bool preserveTerminalFailure = false)
    {
        if (useExternalBaseHost) return;
        while (true)
        {
            CancellationTokenSource? cancellation;
            Task? task;
            lock (referenceStateGate)
            {
                cancellation = referenceCancellation;
                task = referenceTask ?? referenceFinalizationTask;
            }
            if (task is null)
                return;
            var cancellationRequested = false;
            try
            {
                if (cancellation is not null)
                {
                    cancellation.Cancel();
                    cancellationRequested = true;
                }
            }
            catch (ObjectDisposedException) { cancellationRequested = true; }
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Cancellation requested by an urgent caller or disposal is a
                // normal handoff.  A process-abandonment state remains fatal
                // even when the helper reported it as cancellation.
                if (!cancellationRequested && cancellation?.IsCancellationRequested != true)
                    throw;
                if (HasAbandonedReferenceProcess && !preserveTerminalFailure)
                    throw;
            }
            catch (Exception error)
            {
                if (!preserveTerminalFailure
                    || (!HasAbandonedReferenceProcess
                        && error is not ReferenceExtractionProcessException { ProcessMayBeRunning: true }))
                    throw;
            }

            // A new extraction may have been published after the first task
            // completed.  Do not let a caller proceed while that later native
            // owner is still active.
        }
    }

    public async Task SynthesizeAsync(SynthesisRequest request, Resonance.Audio.StreamingAudioBuffer sink, CancellationToken token)
    {
        Interlocked.Increment(ref baseHotLoadRescheduleSuppressed);
        try
        {
            await CancelAndAwaitBaseHotLoadRestoreAsync().ConfigureAwait(false);
            await CancelReferenceExtractionAsync().ConfigureAwait(false);
            ThrowIfReferenceProcessAbandoned();
            await WaitForBaseOwnershipAsync(nameof(SynthesizeAsync), token).ConfigureAwait(false);
            try
            {
                ThrowIfNativeOwnershipPoisoned();
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    ThrowIfNativeOwnershipPoisoned();
                    if (Volatile.Read(ref disposed) != 0)
                        throw new ObjectDisposedException(nameof(RuntimeManager));
                    if (useExternalBaseHost)
                    {
                        var hostBackend = Selection?.Effective.Name
                            ?? throw new InvalidOperationException("TTS backend selection is not ready");
                        if (baseHost is null || !IsBaseHostReadyForBackend(baseHost, hostBackend))
                            await EnsureRuntimeCoreAsync(hostBackend, token).ConfigureAwait(false);
                        var host = baseHost
                            ?? throw new InvalidOperationException("Base runtime host is not ready");
                        if (!IsBaseHostReadyForBackend(host, hostBackend))
                            throw new BaseRuntimeHostException(
                                "Base runtime host is unavailable for the requested backend context");
                        try
                        {
                            NativeOperationStarted("base-host-synthesis");
                            await host.SynthesizeAsync(request, sink, token).ConfigureAwait(false);
                        }
                        finally
                        {
                            if (!configuration.KeepBaseModelLoaded
                                && ReferenceEquals(baseHost, host) && !host.IsBusy)
                            {
                                try
                                {
                                    await host.DisposeAsync().ConfigureAwait(false);
                                    baseHost = null;
                                }
                                catch (Exception error)
                                {
                                    Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                                    ReportNativeFailure(error);
                                    throw;
                                }
                            }
                        }
                        return;
                    }
                    if (runtime is null)
                    {
                        var backendName = Selection?.Effective.Name
                            ?? throw new InvalidOperationException("TTS backend selection is not ready");
                        try { await EnsureRuntimeCoreAsync(backendName, token).ConfigureAwait(false); }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            unhealthy.Add(backendName);
                            var fallback = await TryFallbackAsync(error, token).ConfigureAwait(false);
                            if (fallback is not RuntimeFallbackOutcome.Applied) throw;
                            SelectionChanged?.Invoke(Selection!);
                        }
                    }
                    while (true)
                    {
                        ThrowIfNativeOwnershipPoisoned();
                        var active = runtime ?? throw new InvalidOperationException("TTS runtime is not ready");
                        try
                        {
                            NativeOperationStarted("synthesis");
                            await active.SynthesizeAttemptAsync(request, sink, token).ConfigureAwait(false);
                            return;
                        }
                        catch (Exception error) when (error is not OperationCanceledException && IsBackendFailure(error))
                        {
                            unhealthy.Add(Selection!.Effective.Name);
                            var consumerStarted = sink.ConsumerStarted;
                            sink.DiscardBuffered();
                            var fallback = await TryFallbackAsync(error, token).ConfigureAwait(false);
                            if (fallback is not RuntimeFallbackOutcome.Applied) throw;
                            SelectionChanged?.Invoke(Selection!);
                            if (consumerStarted) throw;
                        }
                    }
                }
                finally { gate.Release(); }
            }
            finally { baseOwnershipGate.Release(); }
        }
        finally
        {
            Interlocked.Decrement(ref baseHotLoadRescheduleSuppressed);
            ScheduleBaseHotLoadRestore();
        }
    }

    private IBaseRuntimeHost CreateBaseHost() => baseHostFactory?.Invoke(
        referenceExtractorPath, talkerPath, codecPath,
        runtimePackDirectory, modelDirectory, referenceExtractionDirectory)
        ?? new BaseRuntimeHostClient(
            referenceExtractorPath, talkerPath, codecPath,
            runtimePackDirectory, modelDirectory, referenceExtractionDirectory);

    private static bool IsBaseHostReadyForBackend(
        IBaseRuntimeHost host, string backendName) =>
        host.IsReady && host.ContextReady
        && String.Equals(host.ActiveBackendId, backendName, StringComparison.Ordinal);

    private async Task EnsureRuntimeCoreAsync(
        string backendName, CancellationToken token, long? hotLoadGeneration = null)
    {
        ThrowIfNativeOwnershipPoisoned();
        if (useExternalBaseHost)
        {
            if (hotLoadGeneration.HasValue
                && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value)) return;
            if (baseHost is { } residentHost && residentHost.IsReady)
            {
                if (IsBaseHostReadyForBackend(residentHost, backendName)) return;
                Interlocked.Exchange(ref switching, 1);
                try
                {
                    NativeOperationStarted("base-host-backend-switch");
                    await residentHost.SwitchBackendAsync(backendName, token)
                        .ConfigureAwait(false);
                    if (!IsBaseHostReadyForBackend(residentHost, backendName))
                        throw new BaseRuntimeHostException(
                            "Base runtime host did not confirm the requested backend context");
                    baseHost = residentHost;
                    return;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    if (error is BaseRuntimeHostException { ProcessMayBeRunning: true })
                    {
                        baseHost = residentHost;
                        Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                        ReportNativeFailure(error);
                    }
                    throw;
                }
                finally { Interlocked.Exchange(ref switching, 0); }
            }
            if (baseHost is { IsReady: false } staleHost)
            {
                try { await staleHost.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is BaseRuntimeHostException
                                               { ProcessMayBeRunning: true })
                {
                    Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                    ReportNativeFailure(error);
                    throw;
                }
                baseHost = null;
            }
            var host = baseHost ?? CreateBaseHost();
            Interlocked.Exchange(ref switching, 1);
            try
            {
                NativeOperationStarted("base-host-start");
                await host.StartAsync(backendName, token).ConfigureAwait(false);
                if (!IsBaseHostReadyForBackend(host, backendName))
                    throw new BaseRuntimeHostException(
                        "Base runtime host did not confirm the requested backend context");
                if (hotLoadGeneration.HasValue
                    && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value))
                {
                    await host.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                baseHost = host;
            }
            catch (Exception error)
            {
                if (error is BaseRuntimeHostException { ProcessMayBeRunning: true })
                {
                    baseHost = host;
                    Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                    ReportNativeFailure(error);
                }
                else if (!ReferenceEquals(baseHost, host))
                {
                    try { await host.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanupError)
                    {
                        Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                        ReportNativeFailure(cleanupError);
                    }
                }
                throw;
            }
            finally { Interlocked.Exchange(ref switching, 0); }
            return;
        }
        if (runtime is not null) return;
        if (hotLoadGeneration.HasValue
            && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value)) return;
        ThrowIfReferenceProcessAbandoned();
        Interlocked.Exchange(ref switching, 1);
        try
        {
            ThrowIfNativeOwnershipPoisoned();
            if (hotLoadGeneration.HasValue
                && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value)) return;
            NativeOperationStarted("runtime-construction");
            var replacement = await Task.Run(() => new QwenCppRuntime(talkerPath, codecPath, backendName), token)
                .ConfigureAwait(false);
            if (hotLoadGeneration.HasValue
                && !IsBaseHotLoadStillSafe(hotLoadGeneration.Value))
            {
                await DisposeUnpublishedRuntimeAsync(replacement).ConfigureAwait(false);
                return;
            }
            if (hotLoadGeneration.HasValue)
            {
                if (!TryPublishHotLoadRuntime(replacement, hotLoadGeneration.Value))
                {
                    await DisposeUnpublishedRuntimeAsync(replacement).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                runtime = replacement;
            }
        }
        finally { Interlocked.Exchange(ref switching, 0); }
    }

    private bool TryPublishHotLoadRuntime(QwenCppRuntime replacement, long generation)
    {
        lock (baseHotLoadGate)
        {
            if (!IsBaseHotLoadStillSafe(generation)) return false;
            runtime = replacement;
            return true;
        }
    }

    private async Task DisposeUnpublishedRuntimeAsync(QwenCppRuntime replacement)
    {
        try { await replacement.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            Interlocked.Exchange(ref runtimeDisposalFailed, 1);
            ReportNativeFailure(error);
            throw;
        }
    }

    private async Task<RuntimeFallbackOutcome> TryFallbackAsync(
        Exception cause, CancellationToken token, long? hotLoadGeneration = null)
    {
        if (Volatile.Read(ref runtimeDisposalFailed) != 0)
        {
            if (hotLoadGeneration.HasValue)
            {
                lock (baseHotLoadGate)
                {
                    if (!IsBaseHotLoadStillSafe(hotLoadGeneration.Value))
                        return RuntimeFallbackOutcome.SkippedUnsafe;
                    if (Selection is not null)
                        Selection = Selection with
                        {
                            Error = $"Inference backend switch stopped because the previous runtime could not be disposed: {cause.Message}"
                        };
                }
            }
            else if (Selection is not null)
                Selection = Selection with
                {
                    Error = $"Inference backend switch stopped because the previous runtime could not be disposed: {cause.Message}"
                };
            return RuntimeFallbackOutcome.Failed;
        }
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
                var replacement = await ReplaceRuntime(candidate.Name, token, hotLoadGeneration)
                    .ConfigureAwait(false);
                if (!replacement)
                    return RuntimeFallbackOutcome.SkippedUnsafe;
                var desired = Selection?.Desired ?? candidate;
                var error = $"Inference backend '{Selection?.Effective.Description ?? desired.Description}' failed: {cause.Message}. " +
                            $"Using '{candidate.Description}' for this session; the configured device remains remembered.";
                var nextSelection = new BackendSelection(
                    desired, candidate, candidate.Type == BackendType.Cpu, error);
                if (hotLoadGeneration.HasValue)
                {
                    if (!TryApplyHotLoadSelection(hotLoadGeneration.Value, nextSelection))
                        return RuntimeFallbackOutcome.SkippedUnsafe;
                }
                else
                {
                    Selection = nextSelection;
                    lastStableSelection = nextSelection;
                }
                return RuntimeFallbackOutcome.Applied;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                unhealthy.Add(candidate.Name);
                cause = error;
            }
        }
        if (hotLoadGeneration.HasValue)
        {
            lock (baseHotLoadGate)
            {
                if (!IsBaseHotLoadStillSafe(hotLoadGeneration.Value))
                    return RuntimeFallbackOutcome.SkippedUnsafe;
                if (Selection is { } staleFailed)
                    Selection = staleFailed with
                    {
                        Error = $"No inference backend is usable. Last failure: {cause.Message}"
                    };
            }
        }
        else if (Selection is { } failed)
            Selection = failed with
            {
                Error = $"No inference backend is usable. Last failure: {cause.Message}"
            };
        return RuntimeFallbackOutcome.Failed;
    }

    private bool TryApplyHotLoadSelection(long generation, BackendSelection selection)
    {
        lock (baseHotLoadGate)
        {
            if (!IsBaseHotLoadStillSafe(generation)) return false;
            Selection = selection;
            lastStableSelection = selection;
            return true;
        }
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

    private void ThrowIfReferenceProcessAbandoned()
    {
        if (Volatile.Read(ref referenceProcessAbandoned) != 0)
            throw new InvalidOperationException(
                "Reference extraction helper could not be terminated safely; Base inference remains stopped until plugin restart");
    }

    private void ThrowIfNativeOwnershipPoisoned()
    {
        if (Volatile.Read(ref referenceProcessAbandoned) != 0)
        {
            throw new InvalidOperationException(
                "Reference extraction helper teardown is incomplete; native ownership is poisoned and restart is required");
        }

        if (Volatile.Read(ref nativeFailureReported) != 0
            || Volatile.Read(ref runtimeDisposalFailed) != 0
            || runtime?.HasTerminalDisposalFailure == true
            || QwenCppRuntime.HasNativeReleaseFailure
            || testSeam?.NativeOwnershipFailure is not null)
        {
            throw new InvalidOperationException(
                "Native inference ownership is poisoned; restart is required before native work can continue");
        }
    }

    private void MarkReferenceProcessAbandoned(Exception failure)
    {
        if (Interlocked.Exchange(ref referenceProcessAbandoned, 1) != 0) return;
        ReportNativeFailure(failure);
    }

    private void ReportNativeFailure(Exception failure)
    {
        if (Interlocked.Exchange(ref nativeFailureReported, 1) != 0) return;
        try { nativeFailureReporter?.Invoke(failure); }
        catch { }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            if (disposeTask is not null) return new ValueTask(disposeTask);
            Interlocked.Exchange(ref disposed, 1);
            disposeTask = DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        ExceptionDispatchInfo? failure = null;
        void Record(Exception error) => failure ??= ExceptionDispatchInfo.Capture(error);

        try { baseHotLoadShutdown.Cancel(); }
        catch (Exception error) { Record(error); }
        try { await CancelReferenceExtractionAsync(preserveTerminalFailure: true).ConfigureAwait(false); }
        catch (Exception error) { Record(error); }

        Task? baseHotLoadTransition;
        Task? baseHotLoadTransitionObserver;
        Task? baseHotLoadObserver;
        Task? baseHotLoadScheduleObserver;
        lock (baseHotLoadGate)
        {
            baseHotLoadTransition = baseHotLoadTransitionTask;
            baseHotLoadTransitionObserver = baseHotLoadTransitionObserverTask;
            baseHotLoadObserver = baseHotLoadObserverTask;
            baseHotLoadScheduleObserver = baseHotLoadScheduleObserverTask;
        }
        if (baseHotLoadTransition is not null)
        {
            try { await baseHotLoadTransition.ConfigureAwait(false); }
            catch (OperationCanceledException) when (baseHotLoadShutdown.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (
                Volatile.Read(ref disposed) != 0 || baseHotLoadShutdown.IsCancellationRequested) { }
            catch (Exception error) { Record(error); }
        }

        foreach (var observer in new[]
                 { baseHotLoadTransitionObserver, baseHotLoadObserver, baseHotLoadScheduleObserver })
        {
            if (observer is null) continue;
            try { await observer.ConfigureAwait(false); }
            catch (Exception error) { Record(error); }
        }

        Task? baseHotLoad;
        lock (baseHotLoadGate) baseHotLoad = baseHotLoadTask;
        if (baseHotLoad is not null)
        {
            try { await baseHotLoad.ConfigureAwait(false); }
            catch (OperationCanceledException) when (baseHotLoadShutdown.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (
                Volatile.Read(ref disposed) != 0 || baseHotLoadShutdown.IsCancellationRequested) { }
            catch (Exception error) { Record(error); }
        }

        var enteredBaseOwnership = false;
        var enteredGate = false;
        try
        {
            await WaitForBaseOwnershipAsync(nameof(DisposeAsync), CancellationToken.None).ConfigureAwait(false);
            enteredBaseOwnership = true;
            await gate.WaitAsync().ConfigureAwait(false);
            enteredGate = true;
            try
            {
                var previousHost = baseHost;
                if (previousHost is not null)
                {
                    Exception? hostDisposalError = null;
                    try { await previousHost.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        hostDisposalError = error;
                        Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                        ReportNativeFailure(error);
                        Record(error);
                    }
                    if (hostDisposalError is not BaseRuntimeHostException
                        { ProcessMayBeRunning: true })
                        baseHost = null;
                }
                var previous = runtime;
                if (previous is not null)
                {
                    try { await previous.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                        ReportNativeFailure(error);
                        throw;
                    }
                }
                runtime = null;
                testSeam?.BaseRuntimeDisposed();
            }
            catch (Exception error) { Record(error); }
        }
        finally
        {
            if (enteredGate) gate.Release();
            if (enteredBaseOwnership) baseOwnershipGate.Release();
        }

        var enteredReferenceGate = false;
        try
        {
            await referenceGate.WaitAsync().ConfigureAwait(false);
            enteredReferenceGate = true;
        }
        catch (Exception error) { Record(error); }
        finally
        {
            if (enteredReferenceGate) referenceGate.Release();
            gate.Dispose();
            referenceGate.Dispose();
            baseOwnershipGate.Dispose();
            baseHotLoadTransitionGate.Dispose();
            baseHotLoadShutdown.Dispose();
        }

        failure?.Throw();
    }
}
