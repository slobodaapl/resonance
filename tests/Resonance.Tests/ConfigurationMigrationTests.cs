using Resonance.Plugin;

namespace Resonance.Tests;

public sealed class ConfigurationMigrationTests
{
    [Theory]
    [InlineData("masculine", "masculine")]
    [InlineData("masculine_adult", "masculine")]
    [InlineData("feminine", "feminine")]
    [InlineData("feminine_adult", "feminine")]
    public void ExplicitLegacySexParses(string archetype, string expectedSex)
    {
        Assert.True(LegacyCastingMigration.TryParseKey($"1185:{archetype}", out var territoryId, out var sex));
        Assert.Equal((uint)1185, territoryId);
        Assert.Equal(expectedSex, sex);
    }

    [Theory]
    [InlineData("neutral")]
    [InlineData("neutral_adult")]
    [InlineData("unknown")]
    [InlineData("masculine_")]
    [InlineData("notmasculine")]
    [InlineData("feminineish")]
    public void UnknownOrNeutralLegacySexStaysUnapplied(string archetype)
    {
        Assert.False(LegacyCastingMigration.TryParseKey($"1185:{archetype}", out _, out _));
    }
}
