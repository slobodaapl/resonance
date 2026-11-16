using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Resonance.Tests;

public sealed class KojinBlendTests
{
    [Fact]
    public void PackedKojinIdentityUsesFiftySoldierThirtySorobanTwentyGenericAndKeepsSoldierPerformance()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Resonance.csproj"))) root = root.Parent;
        Assert.NotNull(root);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root.FullName, "release-assets", "official-profiles", "official-profiles.sqlite3")};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT k.speaker_embedding,g.speaker_embedding,s.speaker_embedding,o.speaker_embedding,
                   k.ref_text,s.ref_text,k.rvq_codes,s.rvq_codes
            FROM fallback_profile k
            JOIN fallback_profile g ON g.domain_id='generic_world' AND g.variant_id=k.variant_id
              AND g.language=k.language AND g.model_hash=k.model_hash
            JOIN official_profile s ON s.group_id='kojinsoldier' AND s.language=k.language AND s.model_hash=k.model_hash
            JOIN official_profile o ON o.group_id='soroban' AND o.language=k.language AND o.model_hash=k.model_hash
            WHERE k.domain_id='kojin'
            """;
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            var actual = MemoryMarshal.Cast<byte, float>((byte[])reader[0]);
            var generic = MemoryMarshal.Cast<byte, float>((byte[])reader[1]);
            var soldier = MemoryMarshal.Cast<byte, float>((byte[])reader[2]);
            var soroban = MemoryMarshal.Cast<byte, float>((byte[])reader[3]);
            Assert.Equal(actual.Length, generic.Length);
            Assert.Equal(actual.Length, soldier.Length);
            Assert.Equal(actual.Length, soroban.Length);
            for (var i = 0; i < actual.Length; i++)
            {
                var expected = 0.5 * soldier[i] + 0.3 * soroban[i] + 0.2 * generic[i];
                var scale = Math.Max(1, Math.Max(Math.Abs(soldier[i]), Math.Max(Math.Abs(soroban[i]), Math.Abs(generic[i]))));
                // Four float roundoffs cover the two staged blends; the oracle sums the requested weights directly.
                var tolerance = 4 * Math.ScaleB(1d, -23) * scale;
                Assert.InRange(Math.Abs(actual[i] - expected), 0, tolerance);
            }
            Assert.Equal(reader.GetString(5), reader.GetString(4));
            Assert.Equal((byte[])reader[7], (byte[])reader[6]);
            count++;
        }
        Assert.Equal(96, count);
    }
}
