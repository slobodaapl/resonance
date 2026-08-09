using Resonance.Game;

namespace Resonance.Tests;

public sealed class OfficialVoiceCatalogTests
{
    [Fact]
    public void AssetIsStrictAndDoesNotFabricateNpcMappingsOrSources()
    {
        var catalog = OfficialVoiceCatalog.Load(ProjectPath("assets", "official-voices.json"));

        Assert.Equal(1, catalog.Version);
        Assert.Equal(["alphinaud", "thancred", "graha-tia"], catalog.Groups.Select(value => value.Id));
        Assert.All(catalog.Groups, group =>
        {
            Assert.Empty(group.NpcBaseIds);
            Assert.Empty(group.Sources);
        });
    }

    [Fact]
    public void ExactNpcIdPrecedesExactLocalizedAliasAndUnknownDoesNotMatch()
    {
        var catalog = OfficialVoiceCatalog.Parse("""
            {
              "schemaVersion": 1,
              "catalogVersion": 2,
              "groups": [
                {
                  "id": "one", "label": "One", "npcBaseIds": [101],
                  "aliases": { "english": ["Shared"] }, "sources": {}
                },
                {
                  "id": "two", "label": "Two", "npcBaseIds": [202],
                  "aliases": { "german": ["Geteilt"] }, "sources": {}
                }
              ]
            }
            """);

        Assert.Equal("one", catalog.Resolve(101, "Geteilt", "german")?.Id);
        Assert.Equal("one", catalog.Resolve(null, "  SHARED ", "english")?.Id);
        Assert.Equal("two", catalog.Resolve(null, "Geteilt", "german")?.Id);
        Assert.Null(catalog.Resolve(null, "Share", "english"));
        Assert.Null(catalog.Resolve(null, "Shared", "german"));
    }

    [Theory]
    [InlineData("[101]", "[101]", "{ \"english\": [\"One\"] }", "{ \"english\": [\"Two\"] }")]
    [InlineData("[]", "[]", "{ \"english\": [\"Same\"] }", "{ \"english\": [\"same\"] }")]
    public void DuplicateExactIdentityMappingsAreRejected(
        string firstIds, string secondIds, string firstAliases, string secondAliases)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "catalogVersion": 1,
              "groups": [
                { "id": "one", "label": "One", "npcBaseIds": {{firstIds}}, "aliases": {{firstAliases}}, "sources": {} },
                { "id": "two", "label": "Two", "npcBaseIds": {{secondIds}}, "aliases": {{secondAliases}}, "sources": {} }
              ]
            }
            """;

        Assert.Throws<InvalidDataException>(() => OfficialVoiceCatalog.Parse(json));
    }

    private static string ProjectPath(params string[] parts)
    {
        var path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "Resonance.csproj")))
            path = Directory.GetParent(path)?.FullName
                   ?? throw new DirectoryNotFoundException("Project root not found");
        return Path.Combine([path, .. parts]);
    }
}
