using System.Collections.ObjectModel;
using System.Text.Json;

namespace Resonance.Tts;

public sealed record CastingPoolSnapshot(
    string? TerritoryPlaceName,
    IReadOnlyList<string> ActiveDomains,
    IReadOnlyDictionary<string, int> ReadyCounts,
    IReadOnlyDictionary<string, int> TargetCounts,
    string? CurrentGeneration,
    IReadOnlyList<string> Failures);

/// <summary>
/// Process-global, lazily activated casting-domain pools. Pool rows are keyed
/// only by domain/language/sex; territory is used to discover domains and to
/// order missing work, never as a persistence key.
/// </summary>
public sealed class CastingDomainPool : IAsyncDisposable
{
    private readonly VoiceRegistry registry;
    private readonly CastingProfileCatalog catalog;
    private readonly Func<VoiceDesigner?> designer;
    private readonly Func<bool> canWork;
    private readonly Func<CancellationToken, Task<bool>>? canWorkAsync;
    private readonly Func<string?> territoryPlaceName;
    private readonly Func<CancellationToken, Task<string?>>? territoryPlaceNameAsync;
    private readonly Func<string> language;
    private readonly Func<CancellationToken, Task<string>>? languageAsync;
    private readonly Func<(int Masculine, int Feminine)> targets;
    private readonly Func<string, string, string, string?>? promptOverride;
    private readonly Func<bool> backgroundEnabled;
    private readonly Func<string, long, string, CancellationToken, Task<VoiceReference>>? designReference;
    private readonly Func<TimeSpan, CancellationToken, Task> waitForCadence;
    private readonly Action? signalCadence;
    private readonly string modelHash;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object gate = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly SemaphoreSlim wake = new(0);
    private readonly object disposeGate = new();
    private readonly HashSet<string> activeDomains = new(StringComparer.Ordinal);
    private readonly HashSet<string> encounteredDomains = new(StringComparer.Ordinal);
    private readonly HashSet<string> manualDomains = new(StringComparer.Ordinal);
    private readonly List<PendingResolution> pendingResolutions = new();
    private readonly Dictionary<string, int> readyCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> targetCounts = new(StringComparer.Ordinal);
    private readonly Queue<string> failures = new();
    private CancellationTokenSource? active;
    private string? currentGeneration;
    private string? activeTerritory;
    private bool activationInitialized;
    private string? lastDomain;
    private string? lastSex = "feminine";
    private readonly Task worker;
    private Task? disposeTask;
    private TaskCompletionSource? operationsDrained;
    private int operationUsers;
    private int disposed;

    private sealed record PendingResolution(
        string DomainId,
        string Language,
        string Sex,
        CastingPoolRequestContext Context);

    public event Action<Exception>? Failed;

    public CastingDomainPool(
        VoiceRegistry registry,
        CastingProfileCatalog catalog,
        Func<VoiceDesigner?> designer,
        Func<bool> canWork,
        Func<string?> territoryPlaceName,
        Func<string> language,
        Func<(int Masculine, int Feminine)> targets,
        string modelHash,
        Func<string, string, string, string?>? promptOverride = null,
        Func<bool>? backgroundEnabled = null,
        Func<CancellationToken, Task<bool>>? canWorkAsync = null,
        Func<CancellationToken, Task<string?>>? territoryPlaceNameAsync = null,
        Func<CancellationToken, Task<string>>? languageAsync = null)
        : this(registry, catalog, designer, canWork, territoryPlaceName, language, targets, modelHash,
            promptOverride, backgroundEnabled, canWorkAsync, territoryPlaceNameAsync,
            languageAsync, null, true, null, null)
    {
    }

    private CastingDomainPool(
        VoiceRegistry registry,
        CastingProfileCatalog catalog,
        Func<VoiceDesigner?> designer,
        Func<bool> canWork,
        Func<string?> territoryPlaceName,
        Func<string> language,
        Func<(int Masculine, int Feminine)> targets,
        string modelHash,
        Func<string, string, string, string?>? promptOverride,
        Func<bool>? backgroundEnabled,
        Func<CancellationToken, Task<bool>>? canWorkAsync,
        Func<CancellationToken, Task<string?>>? territoryPlaceNameAsync,
        Func<CancellationToken, Task<string>>? languageAsync,
        Func<string, long, string, CancellationToken, Task<VoiceReference>>? designReference,
        bool startWorker,
        Func<TimeSpan, CancellationToken, Task>? waitForCadence,
        Action? signalCadence)
    {
        this.registry = registry;
        this.catalog = catalog;
        this.designer = designer;
        this.canWork = canWork;
        this.canWorkAsync = canWorkAsync;
        this.territoryPlaceName = territoryPlaceName;
        this.territoryPlaceNameAsync = territoryPlaceNameAsync;
        this.language = language;
        this.languageAsync = languageAsync;
        this.targets = targets;
        this.modelHash = modelHash;
        this.promptOverride = promptOverride;
        this.backgroundEnabled = backgroundEnabled ?? (() => true);
        this.designReference = designReference;
        this.waitForCadence = waitForCadence ?? (async (delay, token) =>
        {
            await wake.WaitAsync(delay, token).ConfigureAwait(false);
        });
        this.signalCadence = signalCadence;
        worker = startWorker ? Task.Run(RunAsync) : Task.CompletedTask;
    }

    internal static CastingDomainPool CreateForTests(
        VoiceRegistry registry,
        CastingProfileCatalog catalog,
        Func<string, long, string, CancellationToken, Task<VoiceReference>> designReference,
        Func<bool> canWork,
        Func<string?> territoryPlaceName,
        Func<string> language,
        Func<(int Masculine, int Feminine)> targets,
        string modelHash,
        Func<string, string, string, string?>? promptOverride = null,
        Func<bool>? backgroundEnabled = null,
        Func<TimeSpan, CancellationToken, Task>? waitForCadence = null,
        Action? signalCadence = null,
        bool startWorker = false) =>
        new(registry, catalog, () => null, canWork, territoryPlaceName, language, targets, modelHash,
            promptOverride, backgroundEnabled, null, null, null, designReference, startWorker,
            waitForCadence, signalCadence);

    public CastingPoolSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return new(
                    activeTerritory,
                    new ReadOnlyCollection<string>(activeDomains.OrderBy(value => value, StringComparer.Ordinal).ToArray()),
                    new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(readyCounts, StringComparer.Ordinal)),
                    new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(targetCounts, StringComparer.Ordinal)),
                    currentGeneration,
                    new ReadOnlyCollection<string>(failures.ToArray()));
            }
        }
    }

    public void ActivateTerritory(string? placeName, SpeakerCastingEvidence? evidence = null)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var candidates = catalog.GetCandidateDomains(placeName, evidence);
        var territoryDomains = placeName is null
            ? Array.Empty<string>()
            : catalog.GetTerritoryPriors(placeName).Select(prior => prior.DomainId).ToArray();
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (activationInitialized
                && !String.Equals(activeTerritory, placeName, StringComparison.Ordinal))
            {
                activeTerritory = placeName;
                activeDomains.Clear();
                encounteredDomains.Clear();
                lastDomain = null;
                lastSex = "feminine";
            }
            activeTerritory = placeName;
            activationInitialized = true;
            pendingResolutions.RemoveAll(request =>
                !request.Context.FollowsSpeaker
                && !String.Equals(request.Context.TerritoryPlaceName, placeName, StringComparison.Ordinal));
            foreach (var candidate in candidates) activeDomains.Add(candidate.Id);
            foreach (var domain in territoryDomains) activeDomains.Add(domain);
            if (candidates.Count == 0 && territoryDomains.Length == 0)
                activeDomains.Add(catalog.DefaultDomainId);
        }
    }

    public void ResetActivation()
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            activeDomains.Clear();
            encounteredDomains.Clear();
            manualDomains.Clear();
            pendingResolutions.Clear();
            activeTerritory = null;
            activationInitialized = false;
            lastDomain = null;
            lastSex = "feminine";
        }
    }

    public void ActivateResolution(CastingResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (Volatile.Read(ref disposed) != 0) return;
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            activeDomains.Add(resolution.DomainId);
            encounteredDomains.Add(resolution.DomainId);
        }
    }

    public void RequestMissingResolution(
        CastingResolution resolution,
        string language,
        string sex,
        bool followsSpeaker = false)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (Volatile.Read(ref disposed) != 0) return;
        var context = new CastingPoolRequestContext(
            resolution.CatalogVersion,
            resolution.TerritoryPlaceName ?? ActiveTerritory(),
            Array.AsReadOnly(resolution.ModifierIds.ToArray()),
            followsSpeaker);
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            activeDomains.Add(resolution.DomainId);
            encounteredDomains.Add(resolution.DomainId);
            var pending = new PendingResolution(
                resolution.DomainId,
                language,
                sex,
                context);
            if (!pendingResolutions.Any(existing => SamePendingResolution(existing, pending)))
                pendingResolutions.Add(pending);
        }
        SignalWorker();
    }

    public void Pause()
    {
        lock (gate)
        {
            try { active?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task WaitForIdleAsync(CancellationToken token)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        await AcquireOperationAsync(token).ConfigureAwait(false);
        ReleaseOperation();
    }

    public Task RegenerateReadyAsync(CancellationToken token)
    {
        if (Volatile.Read(ref disposed) != 0)
            return Task.FromException(new ObjectDisposedException(nameof(CastingDomainPool)));
        return RegenerateReadyCoreAsync(token);
    }

    private async Task RegenerateReadyCoreAsync(CancellationToken token)
    {
        var placeName = await TerritoryPlaceNameAsync(token).ConfigureAwait(false);
        await RegenerateDomainsAsync(ReachableTerritoryDomains(placeName), token).ConfigureAwait(false);
    }

    public Task RegenerateCurrentTerritoryAsync(CancellationToken token) => RegenerateReadyAsync(token);

    public Task RegenerateDomainAsync(string domainId, CancellationToken token) =>
        RegenerateDomainsAsync([domainId], token);

    public async Task RegenerateDomainsAsync(IEnumerable<string> domainIds, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(domainIds);
        if (Volatile.Read(ref disposed) != 0)
            throw new ObjectDisposedException(nameof(CastingDomainPool));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        Pause();
        await AcquireOperationAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var languageName = await LanguageAsync(linked.Token).ConfigureAwait(false);
            var domains = domainIds.Where(value => !String.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).ToArray();
            await registry.ClearReadySelectedDomainsAsync(domains, languageName, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                foreach (var domain in domains)
                {
                    activeDomains.Add(domain);
                    manualDomains.Add(domain);
                    readyCounts.Remove(CastingPoolScheduler.Key(domain, languageName, "masculine"));
                    readyCounts.Remove(CastingPoolScheduler.Key(domain, languageName, "feminine"));
                }
            }
            if (domains.Length > 0) SignalWorker();
        }
        finally { ReleaseOperation(); }
    }

    private async Task RunAsync()
    {
        while (!shutdown.IsCancellationRequested)
        {
            try { await waitForCadence(TimeSpan.FromSeconds(5), shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            await ExecuteOneWorkAsync(shutdown.Token).ConfigureAwait(false);
        }
    }

    internal async Task<bool> ExecuteOneWorkAsync(CancellationToken token)
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        var currentDesigner = designer();
        var manualRequest = HasManualRequests();
        bool safeToWork;
        try { safeToWork = await CanWorkAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        catch (Exception error)
        {
            Failed?.Invoke(error);
            return false;
        }
        if ((currentDesigner is null && designReference is null)
            || !CastingPoolScheduler.ShouldRun(manualRequest, backgroundEnabled(), safeToWork)) return false;
        using var job = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, token);
        lock (gate) active = job;
        var acquired = false;
        try
        {
            await AcquireOperationAsync(job.Token).ConfigureAwait(false);
            acquired = true;
            var placeName = await TerritoryPlaceNameAsync(job.Token).ConfigureAwait(false);
            ActivateTerritory(placeName);
            var currentLanguage = await LanguageAsync(job.Token).ConfigureAwait(false);
            var configuredTargets = targets();
            var activeNow = ActiveDomains();
            var requested = ManualDomains()
                .Concat(EncounteredDomains())
                .ToArray();
            var priors = WeightedTerritoryDomains(placeName);
            var targetMap = new Dictionary<string, int>(StringComparer.Ordinal);
            var readyMap = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var domain in activeNow)
            {
                foreach (var sex in new[] { "masculine", "feminine" })
                {
                    var key = CastingPoolScheduler.Key(domain, currentLanguage, sex);
                    targetMap[key] = sex == "masculine"
                        ? Math.Max(0, configuredTargets.Masculine)
                        : Math.Max(0, configuredTargets.Feminine);
                    readyMap[key] = await registry.CountReadyDomainPoolAsync(
                        domain, currentLanguage, sex, job.Token).ConfigureAwait(false);
                }
            }
            lock (gate)
            {
                readyCounts.Clear();
                foreach (var pair in readyMap) readyCounts[pair.Key] = pair.Value;
                targetCounts.Clear();
                foreach (var pair in targetMap) targetCounts[pair.Key] = pair.Value;
            }
            RemoveCompletedManualRequests(currentLanguage, readyMap, targetMap);
            var work = CastingPoolScheduler.Order(
                requested, priors, activeNow, currentLanguage, readyMap, targetMap, lastDomain, lastSex)
                .FirstOrDefault();
            if (work is null) return false;
            var attached = AttachPendingResolution(work, placeName);
            await FillOneAsync(attached.Work, placeName, currentDesigner, job).ConfigureAwait(false);
            if (attached.Pending is not null)
                AcknowledgePendingResolution(attached.Pending);
            work = attached.Work;
            lastDomain = work.DomainId;
            lastSex = work.Sex;
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error)
        {
            lock (gate)
            {
                failures.Enqueue(error.Message);
                while (failures.Count > 8) failures.Dequeue();
            }
            Failed?.Invoke(error);
            return false;
        }
        finally
        {
            if (acquired) ReleaseOperation();
            lock (gate)
            {
                if (ReferenceEquals(active, job)) active = null;
                currentGeneration = null;
            }
        }
    }

    private async Task FillOneAsync(
        CastingPoolWorkItem work,
        string? placeName,
        VoiceDesigner? currentDesigner,
        CancellationTokenSource job)
    {
        var token = job.Token;
        var sequence = await registry.ReserveDomainPoolSequenceAsync(
            work.DomainId, work.Language, work.Sex, token).ConfigureAwait(false);
        var domain = catalog.GetDomain(work.DomainId);
        var slots = String.Equals(work.Sex, "feminine", StringComparison.Ordinal)
            ? domain.FeminineSlots
            : domain.MasculineSlots;
        var slot = slots[(int)(sequence % slots.Count)];
        var resolution = BuildDomainResolution(work, placeName);
        var instruction = promptOverride?.Invoke(work.DomainId, work.Language, work.Sex);
        if (String.IsNullOrWhiteSpace(instruction))
            instruction = catalog.BuildPrompt(resolution, work.Language, work.Sex, slot.Id);
        var seed = StableSeed($"{work.DomainId}\0{work.Language}\0{work.Sex}\0{sequence}");
        lock (gate) currentGeneration = $"{work.DomainId}/{work.Language}/{work.Sex}/{slot.Id}";
        var reference = designReference is not null
            ? await designReference(instruction, seed, work.Language, token).ConfigureAwait(false)
            : await (currentDesigner ?? throw new InvalidOperationException("VoiceDesign is unavailable"))
                .DesignReferenceAsync(instruction, seed, work.Language, token).ConfigureAwait(false);
        if (!CastingPoolScheduler.ShouldPersistGeneratedVoice(job.IsCancellationRequested,
                await CanWorkAsync(token).ConfigureAwait(false)))
        {
            job.Cancel();
            token.ThrowIfCancellationRequested();
        }
        token.ThrowIfCancellationRequested();
        var traitsJson = JsonSerializer.Serialize(slot);
        var profile = VoiceRegistry.CreateProfile(
            VoiceProfileKind.Designed,
            work.Language,
            modelHash,
            catalog.Version,
            instruction,
            seed,
            reference,
            sourceMetadata: JsonSerializer.Serialize(new
            {
                domain = work.DomainId,
                sex = work.Sex,
                slot = slot.Id,
                modifiers = resolution.ModifierIds,
            }),
            domainId: work.DomainId,
            catalogVersion: catalog.Version,
            traitsJson: traitsJson);
        token.ThrowIfCancellationRequested();
        await registry.SaveDomainPoolVoiceAsync(
            work.DomainId, work.Language, work.Sex, slot.Id, traitsJson, sequence, profile, token)
            .ConfigureAwait(false);
    }

    private (CastingPoolWorkItem Work, PendingResolution? Pending) AttachPendingResolution(
        CastingPoolWorkItem work,
        string? placeName)
    {
        lock (gate)
        {
            for (var index = pendingResolutions.Count - 1; index >= 0; index--)
            {
                var request = pendingResolutions[index];
                if (!String.Equals(request.DomainId, work.DomainId, StringComparison.Ordinal)
                    || !String.Equals(request.Language, work.Language, StringComparison.Ordinal)
                    || !String.Equals(request.Sex, work.Sex, StringComparison.Ordinal)) continue;
                if (!request.Context.FollowsSpeaker
                    && !String.Equals(request.Context.TerritoryPlaceName, placeName, StringComparison.Ordinal))
                {
                    pendingResolutions.RemoveAt(index);
                    continue;
                }
                return (work with { Context = request.Context }, request);
            }
        }
        return (work, null);
    }

    private void AcknowledgePendingResolution(PendingResolution pending)
    {
        lock (gate)
        {
            for (var index = pendingResolutions.Count - 1; index >= 0; index--)
            {
                if (!ReferenceEquals(pendingResolutions[index], pending)) continue;
                pendingResolutions.RemoveAt(index);
                return;
            }
        }
    }

    private static bool SamePendingResolution(PendingResolution left, PendingResolution right) =>
        String.Equals(left.DomainId, right.DomainId, StringComparison.Ordinal)
        && String.Equals(left.Language, right.Language, StringComparison.Ordinal)
        && String.Equals(left.Sex, right.Sex, StringComparison.Ordinal)
        && left.Context.CatalogVersion == right.Context.CatalogVersion
        && left.Context.FollowsSpeaker == right.Context.FollowsSpeaker
        && String.Equals(left.Context.TerritoryPlaceName, right.Context.TerritoryPlaceName, StringComparison.Ordinal)
        && left.Context.ModifierIds.SequenceEqual(right.Context.ModifierIds, StringComparer.Ordinal);

    private CastingResolution BuildDomainResolution(
        CastingPoolWorkItem work,
        string? placeName)
    {
        var context = work.Context;
        var modifierIds = context?.ModifierIds ?? [];
        var evidence = new SpeakerCastingEvidence(
            "pool", placeName, placeName, ModifierIds: modifierIds);
        var resolution = catalog.Resolve(placeName, evidence);
        if (context is not null)
        {
            return resolution with
            {
                DomainId = work.DomainId,
                ModifierIds = context.ModifierIds,
                CandidateDomainIds = [work.DomainId],
            };
        }

        var territoryModifiers = catalog.GetApplicableTerritoryModifierIds(placeName, work.DomainId, evidence);
        return resolution with
        {
            DomainId = work.DomainId,
            ModifierIds = modifierIds.Concat(territoryModifiers).Distinct(StringComparer.Ordinal).ToArray(),
            CandidateDomainIds = [work.DomainId],
        };
    }

    private IReadOnlyList<string> ActiveDomains()
    {
        lock (gate) return activeDomains.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyList<string> EncounteredDomains()
    {
        lock (gate) return encounteredDomains.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyList<string> ManualDomains()
    {
        lock (gate) return manualDomains.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private string? ActiveTerritory()
    {
        lock (gate) return activeTerritory;
    }

    private bool HasManualRequests()
    {
        lock (gate) return manualDomains.Count > 0;
    }

    private void RemoveCompletedManualRequests(
        string currentLanguage,
        IReadOnlyDictionary<string, int> readyCounts,
        IReadOnlyDictionary<string, int> targetCounts)
    {
        lock (gate)
        {
            foreach (var domain in manualDomains.ToArray())
            {
                var masculine = CastingPoolScheduler.Key(domain, currentLanguage, "masculine");
                var feminine = CastingPoolScheduler.Key(domain, currentLanguage, "feminine");
                var masculineReady = readyCounts.GetValueOrDefault(masculine);
                var feminineReady = readyCounts.GetValueOrDefault(feminine);
                var masculineTarget = targetCounts.GetValueOrDefault(masculine);
                var feminineTarget = targetCounts.GetValueOrDefault(feminine);
                if (masculineReady >= masculineTarget && feminineReady >= feminineTarget)
                    manualDomains.Remove(domain);
            }
        }
    }

    private IReadOnlyList<(string DomainId, double Weight)> WeightedTerritoryDomains(string? placeName)
    {
        if (placeName is null) return [];
        var priors = catalog.GetTerritoryPriors(placeName);
        var total = priors.Sum(prior => prior.Weight);
        if (!(total > 0) || !double.IsFinite(total)) return [];
        return priors
            .OrderByDescending(prior => prior.Weight)
            .ThenBy(prior => prior.DomainId, StringComparer.Ordinal)
            .Select(prior => (prior.DomainId, prior.Weight))
            .ToArray();
    }

    private IReadOnlyList<string> ReachableTerritoryDomains(string? placeName)
    {
        var candidates = catalog.GetCandidateDomains(placeName)
            .Select(domain => domain.Id);
        IEnumerable<string> priors = placeName is null
            ? Enumerable.Empty<string>()
            : catalog.GetTerritoryPriors(placeName).Select(prior => prior.DomainId);
        var domains = candidates.Concat(priors).Distinct(StringComparer.Ordinal).ToArray();
        return domains.Length == 0 ? [catalog.DefaultDomainId] : domains;
    }

    private Task<bool> CanWorkAsync(CancellationToken token) => canWorkAsync is null
        ? Task.FromResult(canWork())
        : canWorkAsync(token);

    private Task<string?> TerritoryPlaceNameAsync(CancellationToken token) => territoryPlaceNameAsync is null
        ? Task.FromResult(territoryPlaceName())
        : territoryPlaceNameAsync(token);

    private Task<string> LanguageAsync(CancellationToken token) => languageAsync is null
        ? Task.FromResult(language())
        : languageAsync(token);

    private void SignalWorker()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try { wake.Release(); }
        catch (ObjectDisposedException) { }
        signalCadence?.Invoke();
    }

    private static long StableSeed(string value)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var character in value) { hash ^= character; hash *= prime; }
        return unchecked((long)hash);
    }

    private async Task AcquireOperationAsync(CancellationToken token)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(CastingDomainPool));
            operationUsers++;
        }
        try { await operations.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            ReleaseOperationAdmission();
            throw;
        }
    }

    private void ReleaseOperation()
    {
        operations.Release();
        ReleaseOperationAdmission();
    }

    private void ReleaseOperationAdmission()
    {
        TaskCompletionSource? drained = null;
        lock (gate)
        {
            if (operationUsers > 0) operationUsers--;
            if (operationUsers == 0)
            {
                drained = operationsDrained;
                operationsDrained = null;
            }
        }
        drained?.TrySetResult();
    }

    private Task WaitForOperationsAsync()
    {
        lock (gate)
        {
            if (operationUsers == 0) return Task.CompletedTask;
            return (operationsDrained ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
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
        try { shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        lock (gate)
        {
            try { active?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        try { wake.Release(); }
        catch (ObjectDisposedException) { }
        try { await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await WaitForOperationsAsync().ConfigureAwait(false);
        lock (gate) active?.Dispose();
        operations.Dispose();
        wake.Dispose();
        shutdown.Dispose();
    }
}
