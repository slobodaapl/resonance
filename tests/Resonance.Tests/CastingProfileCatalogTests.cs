using System.Text.Json;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class CastingProfileCatalogTests
{
    [Fact]
    public void AssetUsesStrictSchemaAndCoversRepresentativeTerritories()
    {
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));

        Assert.Equal(1, catalog.Version);
        Assert.Equal("generic_world", catalog.DefaultDomainId);
        Assert.Empty(catalog.Validate());
        Assert.Empty(catalog.IdentityGroups);
        Assert.Equal(5, catalog.GetDomain("ishgardian").MasculineSlots.Count);
        Assert.Equal(5, catalog.GetDomain("ishgardian").FeminineSlots.Count);
        Assert.Equal(["lominsan"], catalog.GetTerritoryPriors("Mist").Select(prior => prior.DomainId));
        Assert.Empty(catalog.GetTerritoryPriors("Sea of Clouds"));
    }

    [Fact]
    public void ValidationReportsDuplicateReferenceWeightConfidenceSlotsAndCycles()
    {
        var original = File.ReadAllText(ProjectPath("assets", "dub-profiles.json"));

        var duplicate = ReplaceOnce(original, "\"id\": \"kojin\", \"confidence\": \"B\"",
            "\"id\": \"generic_world\", \"confidence\": \"B\"");
        Assert.Contains(CastingProfileCatalog.ValidateJson(duplicate), issue => issue.Code == "duplicate-id");

        var missingReference = ReplaceOnce(original, "\"domainId\": \"lominsan\"", "\"domainId\": \"missing_domain\"");
        Assert.Contains(CastingProfileCatalog.ValidateJson(missingReference), issue => issue.Code == "missing-reference");

        var badWeight = ReplaceOnce(original, "\"weight\": 1", "\"weight\": 0");
        Assert.Contains(CastingProfileCatalog.ValidateJson(badWeight), issue => issue.Code == "invalid-weight");
        var exception = Assert.Throws<CastingProfileCatalogException>(() => CastingProfileCatalog.Parse(badWeight));
        Assert.Contains(exception.Issues, issue => issue.Code == "invalid-weight");

        var badConfidence = ReplaceOnce(original, "\"confidence\": \"B\"", "\"confidence\": \"X\"");
        Assert.Contains(CastingProfileCatalog.ValidateJson(badConfidence), issue => issue.Code == "invalid-confidence");

        var geographicPriorOnNoDomain = ReplaceOnce(original,
            "\"placeName\": \"Wolves' Den Pier\", \"confidence\": \"Ø\", \"priors\": []",
            "\"placeName\": \"Wolves' Den Pier\", \"confidence\": \"Ø\", \"priors\": [{ \"domainId\": \"lominsan\", \"weight\": 1 }]");
        Assert.Contains(CastingProfileCatalog.ValidateJson(geographicPriorOnNoDomain), issue =>
            issue.Code == "forbidden-geographic-prior");
        var geographicException = Assert.Throws<CastingProfileCatalogException>(
            () => CastingProfileCatalog.Parse(geographicPriorOnNoDomain));
        Assert.Contains(geographicException.Issues, issue => issue.Code == "forbidden-geographic-prior");

        var duplicateTerritory = ReplaceOnce(original, "\"placeName\": \"Limsa Lominsa Lower Decks\"",
            "\"placeName\": \"Limsa Lominsa Upper Decks\"");
        Assert.Contains(CastingProfileCatalog.ValidateJson(duplicateTerritory), issue => issue.Code == "duplicate-territory");

        var missingSlot = ReplaceOnce(original,
            "\"masculine\": [\"m_young_light\", \"m_adult_grounded\", \"m_adult_resonant\", \"m_elder_measured\", \"m_adult_formal\"]",
            "\"masculine\": [\"m_young_light\", \"m_adult_grounded\", \"m_adult_resonant\", \"m_elder_measured\"]");
        Assert.Contains(CastingProfileCatalog.ValidateJson(missingSlot), issue => issue.Code == "invalid-slot-count");

        var cycle = ReplaceOnce(original, "\"id\": \"generic_world\", \"confidence\": \"C\"",
            "\"id\": \"generic_world\", \"inherits\": \"kojin\", \"confidence\": \"C\"");
        cycle = ReplaceOnce(cycle, "\"id\": \"kojin\", \"confidence\": \"B\"",
            "\"id\": \"kojin\", \"inherits\": \"generic_world\", \"confidence\": \"B\"");
        Assert.Contains(CastingProfileCatalog.ValidateJson(cycle), issue => issue.Code == "reference-cycle");

        var nullEntries = ReplaceOnce(original, "\"identityGroups\": []", "\"identityGroups\": [null]");
        Assert.Contains(CastingProfileCatalog.ValidateJson(nullEntries), issue => issue.Code == "null-entry");

        var nullSlot = ReplaceOnce(original, "\"slotTemplates\": [\n    {", "\"slotTemplates\": [\n    null,\n    {");
        Assert.Contains(CastingProfileCatalog.ValidateJson(nullSlot), issue => issue.Code == "null-entry");

        var nullCollection = ReplaceJsonCollectionWithNull(original, "rules");
        Assert.Contains(CastingProfileCatalog.ValidateJson(nullCollection), issue => issue.Code == "missing-rules");

        var malformedJson = CastingProfileCatalog.ValidateJson("{");
        Assert.Contains(malformedJson, issue => issue.Code == "malformed-schema");

        var speciesWithoutCategory = ReplaceOnce(original,
            ", \"speakerCategory\": \"non_humanoid\", \"priority\": 110, \"confidence\": \"B\" },\n    { \"id\": \"rule_species_moblin\"",
            ", \"priority\": 110, \"confidence\": \"B\" },\n    { \"id\": \"rule_species_moblin\"");
        Assert.Contains(CastingProfileCatalog.ValidateJson(speciesWithoutCategory), issue =>
            issue.Code == "malformed-rule" && issue.Path.Contains("speakerCategory", StringComparison.Ordinal));
    }

    [Fact]
    public void CuratedEvidenceOutranksIdentityAndTerritoryPrior()
    {
        var catalog = CatalogWithSyntheticIdentity(900001);

        var identity = catalog.Resolve(new SpeakerCastingEvidence("npc:900001", "Radz-at-Han", NpcBaseId: 900001));
        Assert.Equal("alexandrian", identity.DomainId);
        Assert.Equal(CastingEvidenceSource.Identity, identity.Source);
        Assert.DoesNotContain("urban", identity.ModifierIds);

        var nearby = catalog.Resolve(new SpeakerCastingEvidence("npc:900002", "Radz-at-Han", NpcBaseId: 900002));
        Assert.Equal("thavnairian", nearby.DomainId);
        Assert.NotEqual(CastingEvidenceSource.Identity, nearby.Source);

        var identityCandidates = catalog.Resolve(new SpeakerCastingEvidence(
            "npc:900001", "Radz-at-Han", NpcBaseId: 900001, Culture: "Sharlayan"));
        Assert.Equal("sharlayan", identityCandidates.DomainId);
        Assert.Equal(CastingEvidenceSource.Culture, identityCandidates.Source);
        Assert.Equal(["sharlayan", "alexandrian", "thavnairian"], identityCandidates.CandidateDomainIds);

        var ananta = catalog.Resolve(new SpeakerCastingEvidence("ananta:1", "The Peaks", Tribe: "Ananta"));
        Assert.Equal("ananta", ananta.DomainId);
        Assert.Equal(CastingEvidenceSource.Culture, ananta.Source);

        var sharlayan = catalog.Resolve(new SpeakerCastingEvidence("visitor:1", "Thavnair", Culture: "Sharlayan"));
        Assert.Equal("sharlayan", sharlayan.DomainId);
        Assert.Equal(CastingEvidenceSource.Culture, sharlayan.Source);
    }

    [Fact]
    public void StrongCultureRuleBeatsConflictingIdentityAndAppearsFirstAmongCandidates()
    {
        var catalog = CatalogWithSyntheticIdentity(900010);

        var resolution = catalog.Resolve(new SpeakerCastingEvidence(
            "npc:900010", "Thavnair", NpcBaseId: 900010, Culture: "Sharlayan"));

        Assert.Equal("sharlayan", resolution.DomainId);
        Assert.Equal(CastingEvidenceSource.Culture, resolution.Source);
        Assert.Equal(["sharlayan", "alexandrian", "thavnairian"], resolution.CandidateDomainIds);
    }

    [Fact]
    public void StrongTribeSpeciesAndFactionRulesBeatIdentityWithCategorySafeguards()
    {
        var catalog = CatalogWithSyntheticIdentity(900011, 900012, 900013);

        var tribe = catalog.Resolve(new SpeakerCastingEvidence(
            "npc:900011", "The Peaks", NpcBaseId: 900011, Tribe: "Ananta"));
        Assert.Equal("ananta", tribe.DomainId);
        Assert.Equal(CastingEvidenceSource.Culture, tribe.Source);

        var species = catalog.Resolve(new SpeakerCastingEvidence(
            "npc:900012", "The Fringes", NpcBaseId: 900012,
            Species: "Sahagin", Category: "non_humanoid"));
        Assert.Equal("sahagin", species.DomainId);
        Assert.Equal(CastingEvidenceSource.Species, species.Source);

        var humanoidSpecies = catalog.Resolve(new SpeakerCastingEvidence(
            "npc:900012", "The Fringes", NpcBaseId: 900012,
            Species: "Sahagin", Category: "humanoid"));
        Assert.Equal("alexandrian", humanoidSpecies.DomainId);
        Assert.Equal(CastingEvidenceSource.Identity, humanoidSpecies.Source);
        Assert.DoesNotContain("sahagin", humanoidSpecies.CandidateDomainIds);

        var faction = catalog.Resolve(new SpeakerCastingEvidence(
            "npc:900013", "Rak'tika Greatwood", NpcBaseId: 900013,
            Faction: "Night's Blessed"));
        Assert.Equal("rak_tika_night_blessed", faction.DomainId);
        Assert.Equal(CastingEvidenceSource.Faction, faction.Source);
    }

    [Fact]
    public void IdentityOverrideWinsOnlyWhenStrongEvidenceIsAbsent()
    {
        var withIdentity = CatalogWithSyntheticIdentity(900014);
        var withoutIdentity = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
        var evidence = new SpeakerCastingEvidence(
            "npc:900014", "Radz-at-Han", NpcBaseId: 900014);

        var identity = withIdentity.Resolve(evidence);
        var territory = withoutIdentity.Resolve(evidence);

        Assert.Equal("alexandrian", identity.DomainId);
        Assert.Equal(CastingEvidenceSource.Identity, identity.Source);
        Assert.Equal("thavnairian", territory.DomainId);
        Assert.Equal(CastingEvidenceSource.Territory, territory.Source);
        Assert.NotEqual(identity.DomainId, territory.DomainId);
    }

    [Fact]
    public void UnknownOrMissingTerritoryWithoutIdentityFallsToGenericWorld()
    {
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));

        var missing = catalog.Resolve(new SpeakerCastingEvidence("npc:no-territory"));
        Assert.Equal("generic_world", missing.DomainId);
        Assert.Equal(CastingEvidenceSource.Generic, missing.Source);

        var unknown = catalog.Resolve(new SpeakerCastingEvidence("npc:unknown-territory", "Future Place"));
        Assert.Equal("generic_world", unknown.DomainId);
        Assert.Equal(CastingEvidenceSource.Generic, unknown.Source);
        Assert.True(unknown.UnknownTerritory);
    }

    [Fact]
    public void TerritoryMatchingIsExactAndUnknownTerritoriesDiagnoseFallback()
    {
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));

        var exact = catalog.Resolve(new SpeakerCastingEvidence("npc:1", "Thavnair"));
        Assert.Equal("thavnairian", exact.DomainId);

        var substring = catalog.Resolve(new SpeakerCastingEvidence("npc:2", "Old Thavnair settlement"));
        Assert.Equal("generic_world", substring.DomainId);
        Assert.True(substring.UnknownTerritory);
        Assert.Contains("unknown-territory", substring.Diagnostics);
    }

    [Fact]
    public void DocumentedCultureAndSpeciesEvidenceSelectsTheirCuratedDomains()
    {
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));

        Assert.Equal("il_mheg_pixie", catalog.Resolve(new SpeakerCastingEvidence(
            "pixie:1", "Il Mheg", Tribe: "Pixie", Category: "non_humanoid")).DomainId);
        Assert.Equal("il_mheg_nu_mou", catalog.Resolve(new SpeakerCastingEvidence(
            "nu-mou:1", "Il Mheg", Tribe: "Nu Mou", Category: "non_humanoid")).DomainId);
        Assert.Equal("rak_tika_night_blessed", catalog.Resolve(new SpeakerCastingEvidence(
            "blessed:1", "Rak'tika Greatwood", Faction: "Night's Blessed")).DomainId);
        Assert.Equal("loporrit", catalog.Resolve(new SpeakerCastingEvidence(
            "loporrit:1", "Mare Lamentorum", Species: "Loporrit", Category: "non_humanoid")).DomainId);
        Assert.Equal("moblin", catalog.Resolve(new SpeakerCastingEvidence(
            "moblin:1", "Kozama'uka", Species: "Moblin", Category: "non_humanoid")).DomainId);
    }

    [Fact]
    public void MixedTerritoryHasNoGeographicDomainAndTuliyollalWeightsRemainRelative()
    {
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));

        var expedition = catalog.Resolve(new SpeakerCastingEvidence("expedition:1", "Eureka Pyros"));
        Assert.Equal("Ø", catalog.GetTerritory("Eureka Pyros").Confidence);
        Assert.Empty(catalog.GetTerritoryPriors("Eureka Pyros"));
        Assert.Equal("generic_world", expedition.DomainId);
        Assert.False(expedition.HasGeographicPrior);

        var tuliyollal = catalog.GetTerritoryPriors("Tuliyollal");
        Assert.Equal(["southern_turali", "pelupelu", "xbraal", "mamool_ja_tural", "hanuhanu"],
            tuliyollal.Select(prior => prior.DomainId));
        Assert.Equal([30d, 15d, 10d, 10d, 10d], tuliyollal.Select(prior => prior.Weight));
        Assert.Equal(75d, tuliyollal.Sum(prior => prior.Weight));
        var normalized = catalog.GetNormalizedTerritoryPriors("Tuliyollal");
        Assert.InRange(normalized.Sum(prior => prior.Weight), 0.999999d, 1.000001d);
        Assert.Equal(2d, normalized[0].Weight / normalized[1].Weight);

        var heritage = catalog.GetTerritoryPriors("Heritage Found");
        Assert.Equal(["shaaloani_frontier", "alexandrian"], heritage.Select(prior => prior.DomainId));
        Assert.Equal(1d, heritage[0].Weight);
        Assert.Equal(1d, heritage[1].Weight);
        var alexandrian = catalog.Resolve(new SpeakerCastingEvidence("alex:1", "Heritage Found", Culture: "Alexandrian"));
        Assert.Equal("alexandrian", alexandrian.DomainId);

        var radz = catalog.Resolve(new SpeakerCastingEvidence("radz:1", "Radz-at-Han"));
        Assert.Equal("thavnairian", radz.DomainId);
        Assert.Equal(["urban"], radz.ModifierIds);
        var directThavnairian = catalog.Resolve(new SpeakerCastingEvidence(
            "thavnairian:1", "Radz-at-Han", Culture: "Thavnairian"));
        Assert.Equal("thavnairian", directThavnairian.DomainId);
        Assert.Equal(["urban"], directThavnairian.ModifierIds);
        Assert.Equal(["urban"], catalog.GetApplicableTerritoryModifierIds("Radz-at-Han", "thavnairian"));
        Assert.Empty(catalog.GetApplicableTerritoryModifierIds("Radz-at-Han", "sharlayan"));
    }

    [Fact]
    public void SpeciesCategoryFilterAndAcousticModifierDoNotRewriteDialogue()
    {
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));

        var humanoid = catalog.Resolve(new SpeakerCastingEvidence("humanoid:1", "The Fringes", Species: "Sahagin", Category: "humanoid"));
        Assert.Equal("ala_mhigan", humanoid.DomainId);
        Assert.DoesNotContain("sahagin", humanoid.CandidateDomainIds);

        var missingCategory = catalog.Resolve(new SpeakerCastingEvidence("missing:1", "The Fringes", Species: "Sahagin"));
        Assert.Equal("ala_mhigan", missingCategory.DomainId);

        var sahagin = catalog.Resolve(new SpeakerCastingEvidence("sahagin:1", "The Fringes", Species: "Sahagin", Category: "non_humanoid"));
        Assert.Equal("sahagin", sahagin.DomainId);
        var prompt = catalog.BuildPrompt(sahagin, "english", "masculine", evidence: new SpeakerCastingEvidence("sahagin:1", Sex: "masculine"));
        Assert.Contains("never alter dialogue text", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("sahagin:1", prompt, StringComparison.Ordinal);

        var mamoolMissingCategory = catalog.Resolve(new SpeakerCastingEvidence(
            "mamool:1", "The Peaks", Species: "Mamool Ja"));
        Assert.Equal("ala_mhigan", mamoolMissingCategory.DomainId);
        var mamool = catalog.Resolve(new SpeakerCastingEvidence(
            "mamool:2", "The Peaks", Species: "Mamool Ja", Category: "non_humanoid"));
        Assert.Equal("mamool_ja_tural", mamool.DomainId);
    }

    [Fact]
    public void HumanoidCategoryRulesRemainUsableWhileSpeciesRulesStayFailClosed()
    {
        var json = File.ReadAllText(ProjectPath("assets", "dub-profiles.json"));
        json = ReplaceOnce(json, "\"rules\": [",
            "\"rules\": [{ \"id\": \"culture_humanoid_test\", \"kind\": \"culture\", \"value\": \"Civic\", \"domainId\": \"sharlayan\", \"modifierIds\": [], \"speakerCategory\": \"humanoid\", \"priority\": 120, \"confidence\": \"B\" },");
        var catalog = CastingProfileCatalog.Parse(json);

        var humanoid = catalog.Resolve(new SpeakerCastingEvidence(
            "humanoid-rule:1", "The Fringes", Culture: "Civic", Category: "humanoid"));
        Assert.Equal("sharlayan", humanoid.DomainId);

        var nonHumanoid = catalog.Resolve(new SpeakerCastingEvidence(
            "humanoid-rule:2", "The Fringes", Culture: "Civic", Category: "non_humanoid"));
        Assert.Equal("ala_mhigan", nonHumanoid.DomainId);
        Assert.DoesNotContain("sharlayan", nonHumanoid.CandidateDomainIds);
    }

    [Fact]
    public void EverySpeciesRuleRequiresExplicitNonHumanoidCategory()
    {
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
        var expected = new (string Species, string Domain)[]
        {
            ("Loporrit", "loporrit"),
            ("Moblin", "moblin"),
            ("Mamool Ja", "mamool_ja_tural"),
            ("Sahagin", "sahagin"),
            ("Ondo", "ondo"),
            ("Sylph", "sylph"),
            ("Ixal", "ixal"),
            ("Amalj'aa", "amaljaa"),
            ("Kobold", "kobold"),
            ("Vath", "vath"),
            ("Gnath", "gnath"),
            ("Dragon", "dragon"),
            ("Vanu Vanu", "vanu_vanu"),
            ("Moogle", "moogle"),
            ("Goblin", "goblin"),
            ("Fuath", "fuath"),
            ("Qitari", "qitari"),
            ("Kojin", "kojin"),
        };

        foreach (var (species, domain) in expected)
        {
            var accepted = catalog.Resolve(new SpeakerCastingEvidence(
                $"species:{species}", "The Fringes", Species: species, Category: "non_humanoid"));
            Assert.Equal(domain, accepted.DomainId);

            var missingCategory = catalog.Resolve(new SpeakerCastingEvidence(
                $"species-missing:{species}", "The Fringes", Species: species));
            Assert.Equal("ala_mhigan", missingCategory.DomainId);
            Assert.DoesNotContain(domain, missingCategory.CandidateDomainIds);
        }
    }

    [Fact]
    public void StableTerritoryChoiceIsIndependentOfLanguageAndNonEnglishPromptsHaveNoEnglishAccentLabels()
    {
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
        var evidence = new SpeakerCastingEvidence("stable:7", "Tuliyollal", "Tuliyollal", Sex: "feminine");
        var english = catalog.Resolve(evidence);
        var japanese = catalog.Resolve(evidence);
        Assert.Equal(english.DomainId, japanese.DomainId);
        Assert.Equal(english.ModifierIds, japanese.ModifierIds);

        var thavnair = catalog.Resolve(new SpeakerCastingEvidence("voice:1", "Thavnair"));
        var prompt = catalog.BuildPrompt(thavnair, "japanese", "feminine");
        Assert.DoesNotContain("British", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Yorkshire", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Indian English", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("General American", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SlotSelectionScoresKnownAgePhysiqueAndRegisterTraits()
    {
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
        var resolution = catalog.Resolve(new SpeakerCastingEvidence("traits:1", "Foundation"));
        var slot = catalog.SelectBestSlot(resolution, new SpeakerCastingEvidence(
            "traits:1", Sex: "masculine", Age: "elder", Physique: "average"));

        Assert.Equal("m_elder_measured", slot.Id);
        Assert.True(catalog.ScoreSlot(slot, new SpeakerCastingEvidence(
            "traits:1", Age: "elder", Physique: "average")) > 0);
    }

    [Fact]
    public void MissingAgeKeepsPhysicalEvidenceInAdultSlots()
    {
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
        var resolution = catalog.Resolve(new SpeakerCastingEvidence("traits:missing-age", "Foundation"));
        var slot = catalog.SelectBestSlot(resolution, new SpeakerCastingEvidence(
            "traits:missing-age",
            Sex: "masculine",
            Physique: "average",
            BodyType: "average",
            HeightBucket: "average",
            MuscleMassBucket: "low"));

        Assert.Equal("m_adult_grounded", slot.Id);
    }

    [Fact]
    public void RequestedPromptSexWinsAndUnknownTraitsUseAdultNeutralSlot()
    {
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
        var resolution = catalog.Resolve(new SpeakerCastingEvidence("traits:2", "Foundation"));
        var unknownTraits = new SpeakerCastingEvidence("traits:2", Sex: "masculine");

        Assert.Equal("m_adult_grounded", catalog.SelectBestSlot(resolution, unknownTraits).Id);
        var femininePrompt = catalog.BuildPrompt(resolution, "english", "feminine", evidence: unknownTraits);
        Assert.Contains("Adult feminine register; grounded", femininePrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void DeterministicSeedIncludesCatalogVersionButExcludesLanguage()
    {
        var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
        var english = catalog.GetDeterministicSelectionSeed("stable:seed", "Tuliyollal", "english");
        var japanese = catalog.GetDeterministicSelectionSeed("stable:seed", "Tuliyollal", "japanese");
        var differentSpeaker = catalog.GetDeterministicSelectionSeed("other:seed", "Tuliyollal", "english");

        Assert.Equal(english, japanese);
        Assert.NotEqual(english, differentSpeaker);
        Assert.Equal(1, catalog.Version);
    }

    private static string ReplaceOnce(string value, string oldValue, string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Fixture text not found: {oldValue}");
        return value[..index] + newValue + value[(index + oldValue.Length)..];
    }

    private static string ReplaceJsonCollectionWithNull(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        var properties = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        Assert.True(properties.ContainsKey(propertyName), $"JSON property not found: {propertyName}");

        using var nullDocument = JsonDocument.Parse("null");
        properties[propertyName] = nullDocument.RootElement.Clone();
        return JsonSerializer.Serialize(properties);
    }

    private static CastingProfileCatalog CatalogWithSyntheticIdentity(params uint[] npcBaseIds)
    {
        Assert.NotEmpty(npcBaseIds);
        var json = File.ReadAllText(ProjectPath("assets", "dub-profiles.json"));
        var identity = $"\"identityGroups\": [{{ \"id\": \"identity_test\", \"npcBaseIds\": [{string.Join(", ", npcBaseIds)}], \"domainId\": \"alexandrian\", \"modifierIds\": [], \"confidence\": \"A\" }}]";
        return CastingProfileCatalog.Parse(ReplaceOnce(json, "\"identityGroups\": []", identity));
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
