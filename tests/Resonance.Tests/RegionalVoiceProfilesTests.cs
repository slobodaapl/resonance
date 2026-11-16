using System.Text.Json;
using Microsoft.Data.Sqlite;
using Resonance.Data;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class RegionalVoiceProfilesTests
{
    [Fact]
    public async Task MigrationRefreshesAutomaticVoicesByTraitsAndPreservesOfficialCustomAndUnavailableProfiles()
    {
        var token = TestContext.Current.CancellationToken;
        var root = NewRoot();
        try
        {
            using var database = new Database(Path.Combine(root,"voices.sqlite3"));
            var voices = new VoiceRegistry(database);
            var catalog = Catalog();
            var oldBank = await Bank(voices,"feminine_adult",4,"english","model");
            var oldJapanese = await Bank(voices,"feminine_adult",4,"japanese","model");
            var oldOtherModel = await Bank(voices,"feminine_adult",4,"english","other-model");
            var target = await Bank(voices,"feminine_elder",catalog.Version,"english","model");
            var elder = await Speaker(voices,"elder");
            await voices.SaveAndAssignAsync(elder.Id,oldBank,token);
            await voices.SaveAndAssignAsync(elder.Id,oldJapanese,token);
            await voices.SaveAndAssignAsync(elder.Id,oldOtherModel,token);
            var named = await Speaker(voices,"named");
            await voices.SaveAndAssignAsync(named.Id,oldBank,token);
            var official = await voices.SaveAndAssignAsync(named.Id,VoiceRegistry.CreateProfile(
                VoiceProfileKind.Official,"english","model",null,null,null,Reference("official")),token);
            var customSpeaker = await Speaker(voices,"custom");
            await voices.SaveAndAssignAsync(customSpeaker.Id,oldBank,token);
            var custom = await voices.SaveAndAssignAsync(customSpeaker.Id,VoiceRegistry.CreateProfile(
                VoiceProfileKind.Designed,"english","model",4,"user prompt",100,Reference("custom"),
                "{}","kojin",4,"{}"),token);
            var pooledSpeaker = await Speaker(voices,"pooled");
            var pooled = VoiceRegistry.CreateProfile(VoiceProfileKind.Designed,"english","model",4,
                "old pool",1,Reference("old pool"),"{}","kojin",catalog.Version,"{\"Age\":\"elder\",\"Sex\":\"feminine\"}");
            await voices.SaveDomainPoolVoiceAsync("kojin","english","feminine","f_elder_measured",
                pooled.TraitsJson!,0,pooled,token);
            Assert.NotNull(await voices.TryAssignDomainPoolVoiceAsync(pooledSpeaker.Id,"kojin","english",
                "feminine",null,"model",token));

            Assert.Equal(2,await voices.ReconcileRegionalAssignmentsAsync(catalog,"model",token));
            Assert.Equal(target.Id,(await voices.GetBestVoiceAsync(elder.Id,"english","model",token))?.Id);
            Assert.Equal(target.Id,(await voices.GetBestVoiceAsync(pooledSpeaker.Id,"english","model",token))?.Id);
            Assert.Equal(oldJapanese.Id,(await voices.GetBestVoiceAsync(elder.Id,"japanese","model",token))?.Id);
            Assert.Equal(oldOtherModel.Id,(await voices.GetBestVoiceAsync(elder.Id,"english","other-model",token))?.Id);
            Assert.Equal(official.Id,(await voices.GetBestVoiceAsync(named.Id,"english","model",token))?.Id);
            Assert.Equal(custom.Id,(await voices.GetBestVoiceAsync(customSpeaker.Id,"english","model",token))?.Id);
            Assert.Equal(0,await voices.ReconcileRegionalAssignmentsAsync(catalog,"model",token));
            Assert.Equal(oldBank.Id,(await voices.GetBestVoiceByStableKeyAsync(
                VoiceRegistry.DomainFallbackSpeakerKey("kojin","feminine_adult"),"english","model",token))?.Id);
        }
        finally { Directory.Delete(root,true); }
    }

    [Fact]
    public async Task MigrationFailureRollsBackEveryAssignment()
    {
        var token = TestContext.Current.CancellationToken;
        var root = NewRoot();
        try
        {
            using var database = new Database(Path.Combine(root,"voices.sqlite3"));
            var voices = new VoiceRegistry(database);
            var old = await Bank(voices,"feminine_adult",4,"english","model");
            var target = await Bank(voices,"feminine_elder",Catalog().Version,"english","model");
            var first = await Speaker(voices,"first"); var second = await Speaker(voices,"second");
            await voices.SaveAndAssignAsync(first.Id,old,token); await voices.SaveAndAssignAsync(second.Id,old,token);
            await database.ReadAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE TRIGGER reject_regional BEFORE INSERT ON speaker_voice WHEN NEW.speaker_id={second.Id} BEGIN SELECT RAISE(ABORT,'migration rejected'); END";
                return await command.ExecuteNonQueryAsync(token);
            },token);
            await Assert.ThrowsAsync<SqliteException>(() => voices.ReconcileRegionalAssignmentsAsync(Catalog(),"model",token));
            Assert.Equal(old.Id,(await voices.GetBestVoiceAsync(first.Id,"english","model",token))?.Id);
            Assert.Equal(old.Id,(await voices.GetBestVoiceAsync(second.Id,"english","model",token))?.Id);
        }
        finally { Directory.Delete(root,true); }
    }

    [Theory]
    [InlineData(false,4)]
    [InlineData(false,5)]
    [InlineData(true,4)]
    [InlineData(true,5)]
    public async Task BackgroundPoolUsesPreparedAgePerformanceUnlessPromptIsOverridden(bool customPrompt,int referenceCatalogVersion)
    {
        var token = TestContext.Current.CancellationToken;
        var root = NewRoot();
        try
        {
            using var database = new Database(Path.Combine(root,"voices.sqlite3"));
            var voices = new VoiceRegistry(database); var catalog = Catalog();
            var prepared = new Dictionary<string,StoredVoiceProfile>();
            foreach (var age in new[] {"young","adult","elder"})
            {
                var speaker = await voices.ResolveSpeakerAsync(VoiceRegistry.DomainFallbackSpeakerKey("kojin","masculine_"+age),
                    null,"prepared",0,"english",token);
                prepared[age] = await voices.SaveAndAssignAsync(speaker.Id,VoiceRegistry.CreateProfile(
                    VoiceProfileKind.Designed,"english","model",null,null,null,
                    new VoiceReference([8,0],[age.Length],1,1,"prepared "+age),"{}","kojin",referenceCatalogVersion,"{}"),token);
            }
            var designed = new VoiceReference([0,8],[99],1,1,"designed performance");
            await using var pool = CastingDomainPool.CreateForTests(voices,catalog,
                (_,_,_,_)=>Task.FromResult(designed),()=>true,()=>"Kojin",()=>"english",()=> (1,0),"model",
                promptOverride: (_,_,_)=>customPrompt ? "my explicit prompt" : null,backgroundEnabled:()=>false);
            await pool.RegenerateDomainAsync("kojin",token);
            Assert.True(await pool.ExecuteOneWorkAsync(token));
            var npc = await voices.ResolveSpeakerAsync("npc:pool-test",null,"pool",1,"english",token);
            var assigned = await voices.TryAssignDomainPoolVoiceAsync(npc.Id,"kojin","english","masculine",null,"model",token);
            Assert.NotNull(assigned);
            Assert.Equal(customPrompt ? catalog.Version : referenceCatalogVersion,assigned.CatalogVersion);
            if (customPrompt)
            {
                Assert.Equal(designed.Transcript,assigned.Reference.Transcript);
                Assert.Equal(designed.RvqCodes,assigned.Reference.RvqCodes);
                Assert.Equal(designed.SpeakerEmbedding,assigned.Reference.SpeakerEmbedding);
            }
            else
            {
                using var traits = JsonDocument.Parse(assigned.TraitsJson!);
                var expected = prepared[traits.RootElement.GetProperty("Age").GetString()!].Reference;
                Assert.Equal(expected.Transcript,assigned.Reference.Transcript);
                Assert.Equal(expected.RvqCodes,assigned.Reference.RvqCodes);
                Assert.Equal(7.6f,assigned.Reference.SpeakerEmbedding[0],5);
                Assert.Equal(0.4f,assigned.Reference.SpeakerEmbedding[1],5);
            }
        }
        finally { Directory.Delete(root,true); }
    }

    [Theory]
    [InlineData("sylph","default")]
    [InlineData("kojin","feminine")]
    [InlineData("generic_world","feminine_adult")]
    public async Task LegacyFallbacksRemainUsableUntilTheExactNewVariantArrives(string domain,string legacyVariant)
    {
        var root = NewRoot(); var token = TestContext.Current.CancellationToken;
        try
        {
            using var database = new Database(Path.Combine(root,"voices.sqlite3"));
            var voices = new VoiceRegistry(database);
            var legacy = await Bank(voices,legacyVariant,4,"english","model",domain);
            Assert.Equal(legacy.Id,(await voices.GetFallbackVoiceAsync(domain,"feminine_elder","english","model",token))?.Id);
            Assert.Null(await voices.GetFallbackVoiceAsync(domain,"feminine_elder","japanese","model",token));
            Assert.Null(await voices.GetFallbackVoiceAsync(domain,"feminine_elder","english","other",token));
            var current = await Bank(voices,"feminine_elder",Catalog().Version,"english","model",domain);
            Assert.Equal(current.Id,(await voices.GetFallbackVoiceAsync(domain,"feminine_elder","english","model",token))?.Id);
        }
        finally { Directory.Delete(root,true); }
    }

    [Fact]
    public void SharedVoiceRejectsDemographicFallbackDimensions()
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory,"assets","dub-profiles.json")))!;
        var goblin = document["domains"]!.AsArray().Single(domain => domain!["id"]!.GetValue<string>()=="goblin")!;
        goblin["fallbackDimensions"] = "sex_age";
        var error = Assert.Throws<CastingProfileCatalogException>(()=>CastingProfileCatalog.Parse(document.ToJsonString()));
        Assert.Contains(error.Issues,issue=>issue.Code=="invalid-shared-voice");
    }

    [Theory]
    [InlineData("masculine","young")]
    [InlineData("masculine","adult")]
    [InlineData("masculine","elder")]
    [InlineData("feminine","young")]
    [InlineData("feminine","adult")]
    [InlineData("feminine","elder")]
    public async Task SharedGoblinVoiceIgnoresAgeAndSex(string sex,string age)
    {
        var root = NewRoot(); var token = TestContext.Current.CancellationToken;
        try
        {
            using var database = new Database(Path.Combine(root,"voices.sqlite3"));
            var voices = new VoiceRegistry(database); var catalog = Catalog();
            var shared = await Bank(voices,"default",catalog.Version,"english","model","goblin");
            var evidence = new SpeakerCastingEvidence("goblin",Sex:sex,Age:age);
            var resolution = catalog.Resolve(null,evidence) with { DomainId = "goblin" };
            Assert.True(catalog.GetDomain("goblin").SharedVoice);
            var variant = catalog.FallbackVariantId(resolution,evidence);
            Assert.Equal(shared.Id,(await voices.GetFallbackVoiceAsync("goblin",variant,"english","model",token))?.Id);
        }
        finally { Directory.Delete(root,true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedVoiceCreatesNoPoolVariationsUnlessUserOverridesPrompt(bool customPrompt)
    {
        var root = NewRoot(); var token = TestContext.Current.CancellationToken;
        try
        {
            using var database = new Database(Path.Combine(root,"voices.sqlite3"));
            var voices = new VoiceRegistry(database); var catalog = Catalog();
            await using var pool = CastingDomainPool.CreateForTests(voices,catalog,
                (_,_,_,_)=>Task.FromResult(Reference("explicit design")),()=>true,()=>"unknown",()=>"english",()=> (1,0),"model",
                promptOverride:(domain,_,_)=>domain=="goblin" && customPrompt ? "my explicit prompt" : null,
                backgroundEnabled:()=>false);
            await pool.RegenerateDomainAsync("goblin",token);
            var operations = 0;
            while (await pool.ExecuteOneWorkAsync(token)) Assert.True(++operations<=3);
            Assert.Equal(customPrompt ? 1 : 0,await voices.CountReadyDomainPoolAsync("goblin","english","masculine",token));
            Assert.Equal(customPrompt ? 1 : 0,pool.Snapshot.TargetCounts["goblin:english:masculine"]);
        }
        finally { Directory.Delete(root,true); }
    }

    [Fact]
    public async Task SharedVoiceReplacesCurrentVersionAutomaticAssignmentsAndPreservesExplicitVoices()
    {
        var root = NewRoot(); var token = TestContext.Current.CancellationToken;
        try
        {
            using var database = new Database(Path.Combine(root,"voices.sqlite3"));
            var voices = new VoiceRegistry(database); var catalog = Catalog();
            var old = await Bank(voices,"feminine_adult",catalog.Version,"english","model","goblin");
            var shared = await Bank(voices,"default",catalog.Version,"english","model","goblin");
            var automatic = await Speaker(voices,"shared-auto");
            await voices.SaveAndAssignAsync(automatic.Id,old,token);
            var named = await Speaker(voices,"shared-official");
            var official = await voices.SaveAndAssignAsync(named.Id,VoiceRegistry.CreateProfile(
                VoiceProfileKind.Official,"english","model",null,null,null,Reference("official")),token);
            var customNpc = await Speaker(voices,"shared-custom");
            var custom = await voices.SaveAndAssignAsync(customNpc.Id,VoiceRegistry.CreateProfile(
                VoiceProfileKind.Designed,"english","model",null,"user",1,Reference("custom"),"{}","goblin",catalog.Version,"{}"),token);
            foreach (var (sequence,explicitPrompt) in new[] {(0,false),(1,false),(2,true)})
            {
                var profile = VoiceRegistry.CreateProfile(VoiceProfileKind.Designed,"english","model",null,"pool",sequence,
                    Reference("pool"),JsonSerializer.Serialize(new {promptOverride=explicitPrompt,conditioningCatalogVersion=catalog.Version}),
                    "goblin",catalog.Version,"{}");
                await voices.SaveDomainPoolVoiceAsync("goblin","english","masculine","m_adult_grounded","{}",sequence,profile,token);
            }
            var pooled = await Speaker(voices,"shared-pooled");
            Assert.NotNull(await voices.TryAssignDomainPoolVoiceAsync(pooled.Id,"goblin","english","masculine",null,"model",token));
            Assert.Equal(2,await voices.ReconcileRegionalAssignmentsAsync(catalog,"model",token));
            Assert.Equal(shared.Id,(await voices.GetBestVoiceAsync(automatic.Id,"english","model",token))?.Id);
            Assert.Equal(shared.Id,(await voices.GetBestVoiceAsync(pooled.Id,"english","model",token))?.Id);
            Assert.Equal(official.Id,(await voices.GetBestVoiceAsync(named.Id,"english","model",token))?.Id);
            Assert.Equal(custom.Id,(await voices.GetBestVoiceAsync(customNpc.Id,"english","model",token))?.Id);
            Assert.Equal(1,await voices.CountReadyDomainPoolAsync("goblin","english","masculine",token));
            Assert.Equal(0,await voices.ReconcileRegionalAssignmentsAsync(catalog,"model",token));
        }
        finally { Directory.Delete(root,true); }
    }

    [Fact]
    public async Task NewBankWithinSameCatalogRefreshesOlderAutomaticVoicesButKeepsNewerPoolWork()
    {
        var root = NewRoot(); var token = TestContext.Current.CancellationToken;
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var voices = new VoiceRegistry(database); var catalog = Catalog();
            var old = await Bank(voices, "feminine_elder", catalog.Version, "english", "model");
            var automatic = await Speaker(voices, "recipe-refresh");
            await voices.SaveAndAssignAsync(automatic.Id, old, token);
            var oldPool = await Pool(0, false, false);
            var pooled = await Speaker(voices, "recipe-refresh-pooled");
            Assert.Equal(oldPool.Id, (await voices.TryAssignDomainPoolVoiceAsync(
                pooled.Id, "kojin", "english", "feminine", null, "model", token))?.Id);
            var currentPool = await Pool(1, true, false);
            var current = await Speaker(voices, "recipe-refresh-current");
            Assert.Equal(currentPool.Id, (await voices.TryAssignDomainPoolVoiceAsync(
                current.Id, "kojin", "english", "feminine", null, "model", token))?.Id);
            await Pool(2, false, false);
            await Pool(3, false, true);
            var bank = await voices.ResolveSpeakerAsync(VoiceRegistry.DomainFallbackSpeakerKey("kojin", "feminine_elder"),
                null, "kojin", 0, "english", token);
            var updated = await voices.SaveAndAssignAsync(bank.Id, VoiceRegistry.CreateProfile(
                VoiceProfileKind.Designed, "english", "model", null, null, null, Reference("new recipe"),
                "{}", "kojin", catalog.Version, "{}"), token);
            await Timestamp(old.Id, false);
            await database.ReadAsync(async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE voice_profile SET created_utc='2030-01-01T00:00:00.0000000+00:00' WHERE id=$id";
                command.Parameters.AddWithValue("$id", updated.Id);
                return await command.ExecuteNonQueryAsync(token);
            }, token);

            Assert.Equal(2, await voices.ReconcileRegionalAssignmentsAsync(catalog, "model", token));
            Assert.Equal(updated.Id, (await voices.GetBestVoiceAsync(automatic.Id, "english", "model", token))?.Id);
            Assert.Equal(updated.Id, (await voices.GetBestVoiceAsync(pooled.Id, "english", "model", token))?.Id);
            Assert.Equal(currentPool.Id, (await voices.GetBestVoiceAsync(current.Id, "english", "model", token))?.Id);
            Assert.Equal(1, await voices.CountReadyDomainPoolAsync("kojin", "english", "feminine", token));
            Assert.Equal(0, await voices.ReconcileRegionalAssignmentsAsync(catalog, "model", token));

            async Task<StoredVoiceProfile> Pool(int sequence, bool newer, bool custom)
            {
                var profile = VoiceRegistry.CreateProfile(VoiceProfileKind.Designed, "english", "model", null,
                    "pool", sequence, Reference("pool" + sequence),
                    JsonSerializer.Serialize(new { promptOverride = custom, conditioningCatalogVersion = catalog.Version }),
                    "kojin", catalog.Version, "{}");
                var saved = await voices.SaveDomainPoolVoiceAsync("kojin", "english", "feminine", "f_elder_measured",
                    "{}", sequence, profile, token);
                await Timestamp(saved.Id, newer);
                return saved;
            }
            Task<int> Timestamp(string id, bool newer) => database.ReadAsync(async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE voice_profile SET created_utc=$utc WHERE id=$id";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$utc", newer
                    ? "2040-01-01T00:00:00.0000000+00:00" : "2020-01-01T00:00:00.0000000+00:00");
                return await command.ExecuteNonQueryAsync(token);
            }, token);
        }
        finally { Directory.Delete(root, true); }
    }

    private static VoiceReference Reference(string text) => new([1,2],[3],1,1,text);
    private static Task<SpeakerIdentity> Speaker(VoiceRegistry voices,string id) => voices.ResolveSpeakerAsync(
        "npc:"+id,null,id,1,"english",new SpeakerMetadata(Sex:"feminine",Age:"elder"),TestContext.Current.CancellationToken);
    private static async Task<StoredVoiceProfile> Bank(VoiceRegistry voices,string variant,int version,string language,string model,string domain="kojin")
    {
        var token = TestContext.Current.CancellationToken;
        var speaker = await voices.ResolveSpeakerAsync(VoiceRegistry.DomainFallbackSpeakerKey(domain,variant),null,domain,0,language,token);
        return await voices.SaveAndAssignAsync(speaker.Id,VoiceRegistry.CreateProfile(VoiceProfileKind.Designed,
            language,model,null,null,null,Reference(variant+version),"{}",domain,version,"{}"),token);
    }
    private static CastingProfileCatalog Catalog() => CastingProfileCatalog.Load(Path.Combine(AppContext.BaseDirectory,"assets","dub-profiles.json"));
    private static string NewRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory,"regional-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
}
