using System.Text;
using Microsoft.Data.Sqlite;
using Resonance.Game;

namespace Resonance.Tests;

public sealed class CutscenePlanStoreTests
{
    [Fact]
    public void HashOnlyPackJoinsLocalRowsAndPreservesDiamondEdges()
    {
        var root = CreateRoot();
        try
        {
            const string sheet = "quest/000/Test_00001";
            var rows = new Dictionary<string, string>
            {
                ["TEXT_TEST_00001_LEFT_000_0001"] = "Left",
                ["TEXT_TEST_00001_RIGHT_000_0001"] = "Right",
                ["TEXT_TEST_00001_MERGE_000_0002"] = "Merge",
            };
            CreatePack(root, connection =>
            {
                InsertNode(connection, 1, "english", "left", sheet, rows.Keys.ElementAt(0), 1);
                InsertNode(connection, 1, "english", "right", sheet, rows.Keys.ElementAt(1), 1);
                InsertNode(connection, 1, "english", "merge", sheet, rows.Keys.ElementAt(2), 2);
                InsertEdge(connection, 1, "english", null, "left");
                InsertEdge(connection, 1, "english", null, "right");
                InsertEdge(connection, 1, "english", "left", "merge");
                InsertEdge(connection, 1, "english", "right", "merge");
                InsertEdge(connection, 1, "english", "merge", null);
            });
            var cutb = Encoding.UTF8.GetBytes(sheet + "\0");

            var manifest = new CutscenePlanStore(root).TryLoad(
                1, "ffxiv/test/test", "english", cutb,
                name => name == sheet ? rows : null);

            Assert.NotNull(manifest);
            Assert.Equal(2, manifest.StartNodes.Count);
            var right = manifest.Lines.Single(line => line.NodeId == "right");
            Assert.Equal("Merge", Assert.Single(manifest.Successors([right])).Text);
        }
        finally { TestDirectory.Delete(root); }
    }

    [Fact]
    public void DanglingPackEdgeFailsClosed()
    {
        var root = CreateRoot();
        try
        {
            const string sheet = "quest/000/Test_00001";
            const string key = "TEXT_TEST_00001_NPC_000_0001";
            CreatePack(root, connection =>
            {
                InsertNode(connection, 1, "english", "node", sheet, key, 1);
                InsertEdge(connection, 1, "english", null, "missing");
                InsertEdge(connection, 1, "english", "node", null);
            });

            Assert.Throws<InvalidDataException>((Action)(() => new CutscenePlanStore(root).TryLoad(
                1, "ffxiv/test/test", "english", Encoding.UTF8.GetBytes(sheet + "\0"),
                _ => new Dictionary<string, string> { [key] = "Line" })));
        }
        finally { TestDirectory.Delete(root); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "artifact", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CreatePack(string root, Action<SqliteConnection> populate)
    {
        var directory = Path.Combine(root, "official-profile-pack");
        Directory.CreateDirectory(directory);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "official-profiles.sqlite3")}");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE scene_node(
                  cutscene_id INTEGER,language TEXT,node_id TEXT,sheet_key_hash TEXT,
                  occurrence INTEGER,actor_npc_base_id INTEGER,official_group_id TEXT,
                  actor_token_hash TEXT,node_kind TEXT);
                CREATE TABLE scene_edge(
                  cutscene_id INTEGER,language TEXT,current_node_id TEXT,next_node_id TEXT);
                """;
            command.ExecuteNonQuery();
        }
        populate(connection);
    }

    private static void InsertNode(SqliteConnection connection, uint cutscene, string language,
        string node, string sheet, string key, int occurrence)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO scene_node VALUES($c,$l,$n,$h,$o,NULL,NULL,NULL,'synthetic')";
        command.Parameters.AddWithValue("$c", cutscene);
        command.Parameters.AddWithValue("$l", language);
        command.Parameters.AddWithValue("$n", node);
        command.Parameters.AddWithValue("$h", CutscenePlanStore.HashSheetKey(sheet, key));
        command.Parameters.AddWithValue("$o", occurrence);
        command.ExecuteNonQuery();
    }

    private static void InsertEdge(SqliteConnection connection, uint cutscene, string language,
        string? current, string? next)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO scene_edge VALUES($c,$l,$current,$next)";
        command.Parameters.AddWithValue("$c", cutscene);
        command.Parameters.AddWithValue("$l", language);
        command.Parameters.AddWithValue("$current", (object?)current ?? DBNull.Value);
        command.Parameters.AddWithValue("$next", (object?)next ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
