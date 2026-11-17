using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Resonance.Bootstrap;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class ApprovedVoiceBankTests
{
    private static string Project(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Resonance.csproj")))
            directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException(), relative);
    }

    private static SqliteConnection OpenPack()
    {
        var connection = new SqliteConnection($"Data Source={Project("release-assets/official-profiles/official-profiles.sqlite3")};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    [Fact]
    public void ProtectedManualVoicesRetainApprovedIdentity()
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(Project("tests/Resonance.Tests/protected-voice-profiles.json")));
        using var connection = OpenPack();
        foreach (var item in expected.RootElement.GetProperty("profiles").EnumerateArray())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ref_text,speaker_embedding,rvq_codes,rvq_length,codebooks,profile_hash FROM fallback_profile WHERE domain_id=$domain AND variant_id=$variant AND language=$language AND model_hash=$model";
            foreach (var name in new[] { "domain", "variant", "language", "model" })
                command.Parameters.AddWithValue("$" + name, item.GetProperty(name).GetString());
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            var embedding = MemoryMarshal.Cast<byte, float>((byte[])reader[1]).ToArray();
            var codes = MemoryMarshal.Cast<byte, int>((byte[])reader[2]).ToArray();
            var reference = new VoiceReference(embedding, codes, reader.GetInt32(3), reader.GetInt32(4), reader.GetString(0));
            var profile = VoiceRegistry.CreateProfile(VoiceProfileKind.Designed, item.GetProperty("language").GetString()!,
                item.GetProperty("model").GetString()!, null, null, null, reference, "{}", item.GetProperty("domain").GetString(),
                expected.RootElement.GetProperty("catalogVersion").GetInt32(), "{}");
            Assert.Equal(item.GetProperty("profileHash").GetString(), profile.ProfileHash);
            Assert.Equal(profile.ProfileHash, reader.GetString(5));
            Assert.Equal(16, reference.Codebooks);
            Assert.Equal(reference.RvqLength * 16, reference.RvqCodes.Length);
        }
    }

    [Fact]
    public void NuMouUsesNinetyPercentEnglishNativeAndTenPercentAgeGenderFoundation()
    {
        using var connection = OpenPack();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.speaker_embedding,g.speaker_embedding,b.speaker_embedding,n.ref_text,b.ref_text,n.rvq_codes,b.rvq_codes
            FROM fallback_profile n
            JOIN fallback_profile g ON g.domain_id='generic_world' AND g.variant_id=n.variant_id
              AND g.language=n.language AND g.model_hash=n.model_hash
            JOIN official_profile b ON b.group_id='beqlugg' AND b.language='english' AND b.model_hash=n.model_hash
            WHERE n.domain_id='il_mheg_nu_mou'
            """;
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            var actual = MemoryMarshal.Cast<byte, float>((byte[])reader[0]);
            var generic = MemoryMarshal.Cast<byte, float>((byte[])reader[1]);
            var native = MemoryMarshal.Cast<byte, float>((byte[])reader[2]);
            Assert.Equal(native.Length, actual.Length);
            Assert.Equal(generic.Length, actual.Length);
            for (var i = 0; i < actual.Length; i++)
                Assert.InRange(Math.Abs(actual[i] - (.9 * native[i] + .1 * generic[i])), 0, 1e-6 * Math.Max(1, Math.Abs(actual[i])));
            Assert.Equal(reader.GetString(4), reader.GetString(3));
            Assert.Equal((byte[])reader[6], (byte[])reader[5]);
            count++;
        }
        Assert.Equal(96, count);
    }

    [Fact]
    public void ChildrenUseProvidedTranscriptAndBenchmarkUsesApprovedGenericVoice()
    {
        using var models = JsonDocument.Parse(File.ReadAllText(Project("assets/models.json")));
        using var connection = OpenPack();
        foreach (var model in models.RootElement.GetProperty("artifacts").EnumerateArray()
                     .Where(value => value.GetProperty("id").GetString()!.StartsWith("base-", StringComparison.Ordinal)))
        {
            var benchmark = BenchmarkVoice.Load(model.GetProperty("fileName").GetString()!);
            BaseHostProtocol.ValidateVoiceReferencePayload(benchmark);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT variant_id,ref_text,speaker_embedding,rvq_codes FROM fallback_profile WHERE domain_id='generic_world' AND model_hash=$model";
            command.Parameters.AddWithValue("$model", model.GetProperty("sha256").GetString());
            using var reader = command.ExecuteReader();
            var children = 0;
            var adults = 0;
            while (reader.Read())
            {
                if (reader.GetString(0).EndsWith("_young", StringComparison.Ordinal))
                {
                    Assert.Equal("Hello sir! I am looking for my mother, have you seen her anywhere? Oh no! There are monsters coming!", reader.GetString(1));
                    children++;
                }
                if (reader.GetString(0) != "masculine_adult") continue;
                Assert.Equal(benchmark.Transcript, reader.GetString(1));
                Assert.Equal(benchmark.SpeakerEmbedding, MemoryMarshal.Cast<byte, float>((byte[])reader[2]).ToArray());
                Assert.Equal(benchmark.RvqCodes, MemoryMarshal.Cast<byte, int>((byte[])reader[3]).ToArray());
                adults++;
            }
            Assert.Equal(8, children);
            Assert.Equal(4, adults);
        }
    }
}
