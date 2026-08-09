using Resonance.Tts;

namespace Resonance.Tests;

public sealed class CastingPoolSchedulerTests
{
    [Fact]
    public void RequestedMissingDomainAndOppositeSexComeFirst()
    {
        var ready = new Dictionary<string, int>
        {
            [CastingPoolScheduler.Key("identity", "english", "masculine")] = 0,
            [CastingPoolScheduler.Key("identity", "english", "feminine")] = 0,
            [CastingPoolScheduler.Key("territory", "english", "masculine")] = 0,
            [CastingPoolScheduler.Key("territory", "english", "feminine")] = 0,
        };
        var targets = ready.Keys.ToDictionary(key => key, _ => 1);

        var order = CastingPoolScheduler.Order(
            ["identity"],
            [],
            ["territory", "identity"],
            "english",
            ready,
            targets,
            lastDomain: "territory",
            lastSex: "masculine");

        Assert.Equal(("identity", "english", "feminine"),
            (order[0].DomainId, order[0].Language, order[0].Sex));
        Assert.Equal(("identity", "english", "masculine"),
            (order[1].DomainId, order[1].Language, order[1].Sex));
    }

    [Fact]
    public void ReadyTargetEqualityRemovesWork()
    {
        var key = CastingPoolScheduler.Key("ishgardian", "english", "feminine");
        var ready = new Dictionary<string, int> { [key] = 5 };
        var targets = new Dictionary<string, int> { [key] = 5 };

        var order = CastingPoolScheduler.Order(
            ["ishgardian"],
            [],
            [],
            "english",
            ready,
            targets,
            lastDomain: null,
            lastSex: null);

        Assert.Empty(order);
    }

    [Fact]
    public void ExplicitRequestCanScheduleInactiveDomainWhenBackgroundIsDisabled()
    {
        var key = CastingPoolScheduler.Key("manual", "english", "masculine");
        var order = CastingPoolScheduler.Order(
            ["manual"],
            [],
            ["territory"],
            "english",
            new Dictionary<string, int> { [key] = 0 },
            new Dictionary<string, int> { [key] = 1 },
            lastDomain: "manual",
            lastSex: "feminine");

        Assert.Equal(("manual", "english", "masculine"),
            (order[0].DomainId, order[0].Language, order[0].Sex));
        Assert.True(CastingPoolScheduler.ShouldRun(manualRequest: true, backgroundEnabled: false, safeToWork: true));
        Assert.False(CastingPoolScheduler.ShouldRun(manualRequest: false, backgroundEnabled: false, safeToWork: true));
        Assert.False(CastingPoolScheduler.ShouldRun(manualRequest: true, backgroundEnabled: true, safeToWork: false));
    }

    [Fact]
    public void CancellationOrUnsafeStateCannotPersistGeneratedVoice()
    {
        Assert.False(CastingPoolScheduler.ShouldPersistGeneratedVoice(cancellationRequested: true, safeToWork: true));
        Assert.False(CastingPoolScheduler.ShouldPersistGeneratedVoice(cancellationRequested: false, safeToWork: false));
        Assert.True(CastingPoolScheduler.ShouldPersistGeneratedVoice(cancellationRequested: false, safeToWork: true));
    }

    [Fact]
    public void RequestedTierStaysAheadOfWeightedPriorsAndGeneralActiveDomains()
    {
        var domains = new[] { "requested", "high", "low", "general" };
        var ready = domains.ToDictionary(
            domain => CastingPoolScheduler.Key(domain, "english", "masculine"), _ => 0);
        var targets = ready.Keys.ToDictionary(key => key, _ => 1);

        var order = CastingPoolScheduler.Order(
            ["requested"],
            [("low", 1d), ("high", 3d)],
            ["general", "low", "high"],
            "english",
            ready,
            targets,
            lastDomain: "requested",
            lastSex: "feminine");

        Assert.Equal("requested", order[0].DomainId);
        Assert.Equal("high", order[1].DomainId);
        Assert.Equal("low", order[2].DomainId);
        Assert.Equal("general", order[3].DomainId);
    }

    [Fact]
    public void LastDomainCannotDemoteHigherWeightPrior()
    {
        var ready = new Dictionary<string, int>
        {
            [CastingPoolScheduler.Key("high", "english", "masculine")] = 0,
            [CastingPoolScheduler.Key("low", "english", "masculine")] = 0,
        };
        var targets = ready.Keys.ToDictionary(key => key, _ => 1);

        var order = CastingPoolScheduler.Order(
            [],
            [("low", 1d), ("high", 3d)],
            ["low", "high"],
            "english",
            ready,
            targets,
            lastDomain: "high",
            lastSex: "feminine");

        Assert.Equal("high", order[0].DomainId);
        Assert.Equal("low", order[1].DomainId);
    }
}
