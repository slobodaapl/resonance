using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Resonance.Game;

public sealed class CutscenePlanStore(string dataDirectory)
{
    private readonly string databasePath = Path.Combine(
        Path.GetFullPath(dataDirectory), "official-profile-pack", "official-profiles.sqlite3");

    public CutsceneVoiceManifest? TryLoad(
        uint cutsceneId,
        string cutscenePath,
        string language,
        ReadOnlySpan<byte> cutb,
        Func<string, IReadOnlyDictionary<string, string>?> loadSheet)
    {
        if (!File.Exists(databasePath)) return null;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        connection.Open();
        if (!TableExists(connection, "scene_node") || !TableExists(connection, "scene_edge")) return null;

        var textByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sheetName in CutsceneVoiceManifestParser.ExtractDialogueSheetNames(cutb))
        {
            var sheet = loadSheet(sheetName);
            if (sheet is null) continue;
            foreach (var (key, text) in sheet)
                textByHash.TryAdd(HashSheetKey(sheetName, key), text);
        }

        var lines = new List<CutsceneVoiceLine>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT node_id,sheet_key_hash,occurrence,official_group_id,node_kind,
                       actor_token_hash,actor_npc_base_id
                FROM scene_node WHERE cutscene_id=$cutscene AND language=$language
                ORDER BY occurrence,node_id
                """;
            command.Parameters.AddWithValue("$cutscene", cutsceneId);
            command.Parameters.AddWithValue("$language", language);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var nodeId = reader.GetString(0);
                var keyHash = reader.GetString(1);
                if (!textByHash.TryGetValue(keyHash, out var text))
                    throw new InvalidDataException(
                        $"Scene plan node '{nodeId}' has no matching local dialogue key");
                var occurrence = reader.GetInt32(2);
                var group = reader.IsDBNull(3) ? null : reader.GetString(3);
                var kind = reader.GetString(4);
                var actorHash = reader.IsDBNull(5) ? null : reader.GetString(5);
                var npcBaseId = reader.IsDBNull(6) ? null : checked((uint?)reader.GetInt64(6));
                lines.Add(new CutsceneVoiceLine(
                    keyHash, group ?? String.Empty, text, kind == "native", null,
                    occurrence, occurrence, kind == "choice", group, actorHash, npcBaseId)
                    { NodeId = nodeId });
            }
        }
        if (lines.Count == 0) return null;

        var edges = new List<CutsceneVoiceEdge>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT current_node_id,next_node_id FROM scene_edge
                WHERE cutscene_id=$cutscene AND language=$language
                """;
            command.Parameters.AddWithValue("$cutscene", cutsceneId);
            command.Parameters.AddWithValue("$language", language);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                edges.Add(new(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1)));
        }
        Validate(lines, edges);
        return new(cutsceneId, cutscenePath, lines, edges);
    }

    public static string HashSheetKey(string sheetName, string key)
    {
        var bytes = Encoding.UTF8.GetBytes(sheetName + "\0" + key);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void Validate(
        IReadOnlyList<CutsceneVoiceLine> lines, IReadOnlyList<CutsceneVoiceEdge> edges)
    {
        var ids = lines.Select(line => line.NodeId).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != lines.Count) throw new InvalidDataException("Scene plan contains duplicate node IDs");
        if (!edges.Any(edge => edge.CurrentNodeId is null && edge.NextNodeId is not null)
            || !edges.Any(edge => edge.CurrentNodeId is not null && edge.NextNodeId is null))
            throw new InvalidDataException("Scene plan has no complete start/end boundary");
        foreach (var edge in edges)
        {
            if (edge.CurrentNodeId is null && edge.NextNodeId is null)
                throw new InvalidDataException("Scene plan contains an empty edge");
            if (edge.CurrentNodeId is not null && !ids.Contains(edge.CurrentNodeId)
                || edge.NextNodeId is not null && !ids.Contains(edge.NextNodeId))
                throw new InvalidDataException("Scene plan contains a dangling edge");
        }
        if (edges.Distinct().Count() != edges.Count)
            throw new InvalidDataException("Scene plan contains a duplicate edge");
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name)";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }
}
