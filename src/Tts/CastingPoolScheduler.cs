namespace Resonance.Tts;

public sealed record CastingPoolRequestContext(
    int CatalogVersion,
    string? TerritoryPlaceName,
    IReadOnlyList<string> ModifierIds,
    bool FollowsSpeaker);

public sealed record CastingPoolWorkItem(
    string DomainId,
    string Language,
    string Sex,
    CastingPoolRequestContext? Context = null);

/// <summary>Pure ordering boundary for the background domain/sex scheduler.</summary>
public static class CastingPoolScheduler
{
    public static IReadOnlyList<CastingPoolWorkItem> Order(
        IEnumerable<string> requestedDomains,
        IEnumerable<(string DomainId, double Weight)> territoryPriors,
        IEnumerable<string> activeDomains,
        string language,
        IReadOnlyDictionary<string, int> readyCounts,
        IReadOnlyDictionary<string, int> targetCounts,
        string? lastDomain,
        string? lastSex)
    {
        var requested = DistinctDomains(requestedDomains);
        var requestedSet = requested.ToHashSet(StringComparer.Ordinal);
        var priors = territoryPriors
            .Where(prior => !String.IsNullOrWhiteSpace(prior.DomainId)
                && double.IsFinite(prior.Weight) && prior.Weight > 0
                && !requestedSet.Contains(prior.DomainId))
            .GroupBy(prior => prior.Weight)
            .OrderByDescending(group => group.Key)
            .SelectMany(group => RotateWithinTier(
                group.Select(prior => prior.DomainId), lastDomain))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var priorSet = priors.ToHashSet(StringComparer.Ordinal);
        var general = DistinctDomains(activeDomains)
            .Where(domain => !requestedSet.Contains(domain) && !priorSet.Contains(domain));
        var orderedDomains = RotateWithinTier(requested, lastDomain)
            .Concat(priors)
            .Concat(RotateWithinTier(general, lastDomain))
            .ToArray();
        var sexes = String.Equals(lastSex, "masculine", StringComparison.Ordinal)
            ? new[] { "feminine", "masculine" }
            : new[] { "masculine", "feminine" };
        var result = new List<CastingPoolWorkItem>(orderedDomains.Length * 2);
        foreach (var domain in orderedDomains)
        {
            foreach (var sex in sexes)
            {
                var key = Key(domain, language, sex);
                var ready = readyCounts.GetValueOrDefault(key);
                var target = targetCounts.GetValueOrDefault(key);
                if (ready < target) result.Add(new(domain, language, sex));
            }
        }
        return result;
    }

    public static bool ShouldRun(bool manualRequest, bool backgroundEnabled, bool safeToWork) =>
        safeToWork && (manualRequest || backgroundEnabled);

    public static bool ShouldPersistGeneratedVoice(bool cancellationRequested, bool safeToWork) =>
        !cancellationRequested && safeToWork;

    private static IReadOnlyList<string> DistinctDomains(IEnumerable<string> domains) => domains
        .Where(domain => !String.IsNullOrWhiteSpace(domain))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static IEnumerable<string> RotateWithinTier(IEnumerable<string> domains, string? lastDomain)
    {
        var values = domains.Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length <= 1 || lastDomain is null) return values;
        return values.Where(domain => !String.Equals(domain, lastDomain, StringComparison.Ordinal))
            .Concat(values.Where(domain => String.Equals(domain, lastDomain, StringComparison.Ordinal)));
    }

    public static string Key(string domainId, string language, string sex) =>
        $"{domainId}:{language}:{sex}";
}
