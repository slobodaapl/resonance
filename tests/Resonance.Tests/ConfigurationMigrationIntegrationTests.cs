using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class ConfigurationMigrationIntegrationTests
{
    [Fact]
    public void RuntimeOptionDefaultsAreIntentional()
    {
        Assert.True(new Configuration().PreDubUpcomingCutscenes);
        Assert.False(new Configuration().AutoAdvanceDiagnostics);
        Assert.False(new Configuration().KeepBaseModelLoaded);
        Assert.False(new Configuration().DisableVoicePackAutoUpdate);
        Assert.False(new Configuration().ExportDebugBaseWav);
    }

    [Fact]
    public void MissingPreDubSettingDeserializesEnabled()
    {
        var configuration = System.Text.Json.JsonSerializer.Deserialize<Configuration>("{}");

        Assert.NotNull(configuration);
        Assert.True(configuration.PreDubUpcomingCutscenes);
    }

    [Fact]
    public void UnknownAndNeutralLegacyArchetypesAreRetainedWithDiagnostics()
    {
        var configuration = new Configuration
        {
            Version = 1,
            PaletteOverrides = new Dictionary<string, string>
            {
                ["1:neutral_adult"] = "neutral prompt",
                ["1:unknown"] = "unknown prompt",
            },
        };
        var diagnostics = new List<string>();
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));

        configuration.MigrateCastingV2(
            catalog,
            territoryId => territoryId == 1 ? "Mist" : null,
            diagnostics.Add);

        Assert.Equal(2, configuration.Version);
        Assert.Null(configuration.PaletteOverrides);
        Assert.Equal("neutral prompt", configuration.LegacyUnappliedOverrides["1:neutral_adult"]);
        Assert.Equal("unknown prompt", configuration.LegacyUnappliedOverrides["1:unknown"]);
        Assert.Empty(configuration.PromptOverrides);
        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, message => Assert.Contains("Retained legacy override", message, StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyExplicitSexAndSingleDomainTerritoryMigrate()
    {
        var configuration = new Configuration
        {
            Version = 1,
            PaletteOverrides = new Dictionary<string, string>
            {
                ["1:feminine_adult"] = "feminine prompt",
                ["1:masculine_adult"] = "masculine prompt",
                ["2:masculine_adult"] = "ambiguous prompt",
            },
        };
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));

        configuration.MigrateCastingV2(
            catalog,
            territoryId => territoryId switch
            {
                1 => "Mist",
                2 => "Tuliyollal",
                _ => null,
            });

        Assert.Equal("feminine prompt", configuration.GetPromptOverride("lominsan", "english", "feminine"));
        Assert.Equal("masculine prompt", configuration.GetPromptOverride("lominsan", "english", "masculine"));
        Assert.Equal("ambiguous prompt", configuration.LegacyUnappliedOverrides["2:masculine_adult"]);
        Assert.Single(configuration.LegacyUnappliedOverrides);
    }

    private static string ProjectPath(params string[] parts)
    {
        var path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "Resonance.csproj")))
        {
            path = Directory.GetParent(path)?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate Resonance project root");
        }
        return Path.Combine([path, .. parts]);
    }
}
