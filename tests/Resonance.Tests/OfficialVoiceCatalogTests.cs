using Resonance.Game;

namespace Resonance.Tests;

public sealed class OfficialVoiceCatalogTests
{
    [Fact]
    public void GameAuthoredActorTokenResolvesSpacedOfficialAlias()
    {
        var catalog = OfficialVoiceCatalog.Load(ProjectPath("assets", "official-voices.json"));

        var group = catalog.Resolve(null, "WUKLAMAT", "english");

        Assert.Equal("wuk-lamat", group?.Id);
    }

    [Fact]
    public void ExactGameActorTokenIsLanguageIndependent()
    {
        var catalog = OfficialVoiceCatalog.Parse("""
            {
              "schemaVersion": 1,
              "catalogVersion": 1,
              "groups": [{
                "id": "actor", "label": "Actor", "npcBaseIds": [],
                "aliases": {}, "sources": {}, "actorTokens": ["GAMEACTOR"]
              }]
            }
            """);

        Assert.Equal("actor", catalog.Resolve(null, "GAMEACTOR", "japanese")?.Id);
        Assert.Equal("actor", catalog.GetGroup("actor")?.Id);
    }

    [Fact]
    public void AssetIsStrictAndAcceptsCuratedEnrichment()
    {
        var catalog = OfficialVoiceCatalog.Load(ProjectPath("assets", "official-voices.json"));

        Assert.True(catalog.Version > 0);
        Assert.NotEmpty(catalog.Groups);
        Assert.Equal(catalog.Groups.Count, catalog.Groups.Select(value => value.Id).Distinct().Count());
        Assert.All(catalog.Groups, group =>
        {
            Assert.False(String.IsNullOrWhiteSpace(group.Id));
            Assert.False(String.IsNullOrWhiteSpace(group.Label));
            Assert.NotNull(group.NpcBaseIds);
            Assert.NotNull(group.Aliases);
            Assert.Empty(group.Sources);
            Assert.NotEmpty(group.ExactActorTokens);
            Assert.All(group.ExactActorTokens,
                actorToken => Assert.Same(group, catalog.Resolve(null, actorToken, "french")));
        });
        Assert.Equal(catalog.Groups.SelectMany(group => group.ExactActorTokens).Count(),
            catalog.Groups.SelectMany(group => group.ExactActorTokens)
                .Select(value => new string(value.Where(char.IsLetterOrDigit)
                    .Select(char.ToLowerInvariant).ToArray()))
                .Distinct(StringComparer.Ordinal).Count());
        Assert.All(catalog.Groups.SelectMany(group => group.ExactActorTokens), actorToken =>
            Assert.DoesNotContain("/", actorToken, StringComparison.Ordinal));
    }

    [Fact]
    public void CuratedWukLamatNpcVariantsShareOneOfficialIdentity()
    {
        var catalog = OfficialVoiceCatalog.Load(ProjectPath("assets", "official-voices.json"));
        uint[] variants = [1047302, 1047439, 1047526, 1047567, 1047570, 1047583, 1047595, 1047776];

        Assert.All(variants, id => Assert.Equal("wuk-lamat", catalog.Resolve(id, "irrelevant", "english")?.Id));
        Assert.Equal("wuk-lamat", catalog.Resolve(null, "Wuk Lamat", "english")?.Id);
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
