using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumina;
using Lumina.Data;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Resonance.Game;

if (args.Length is not 3 and not 4)
    throw new ArgumentException(
        "usage: CutscenePlanBuilder <sqpack-directory> <official-voices.json> <plan.json> [enumeration.json]");

var game = new GameData(Path.GetFullPath(args[0]));
var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(args[1]), JsonOptions())
              ?? throw new InvalidDataException("Official voice catalog is empty");
if (catalog.Groups is null) throw new InvalidDataException("Official voice catalog has no groups");
var officialGroups = catalog.Groups
    .SelectMany(group => group.ActorTokens.Select(token => (Token: NormalizeActor(token), group.Id)))
    .ToDictionary(value => value.Token, value => value.Id, StringComparer.Ordinal);
var languages = new[]
{
    (Name: "english", Code: "en", Sheet: Language.English),
    (Name: "japanese", Code: "ja", Sheet: Language.Japanese),
    (Name: "german", Code: "de", Sheet: Language.German),
    (Name: "french", Code: "fr", Sheet: Language.French),
};
var nodes = new List<SceneNode>();
var edges = new List<SceneEdge>();
var actors = new Dictionary<string, Dictionary<string, List<Source>>>(StringComparer.OrdinalIgnoreCase);
var undubbedActorTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var parsedCutscenes = 0;
var missingCutb = 0;

foreach (var row in game.GetExcelSheet<Cutscene>())
{
    var cutscenePath = row.Path.ToString();
    if (String.IsNullOrWhiteSpace(cutscenePath)) continue;
    var cutb = game.GetFile($"cut/{cutscenePath}.cutb");
    if (cutb is null)
    {
        missingCutb++;
        continue;
    }
    var cutbData = cutb.Data!;
    parsedCutscenes++;

    foreach (var language in languages)
    {
        var sheets = LoadSheets(game, cutbData, language.Sheet);
        var manifest = CutsceneVoiceManifestParser.Parse(
            row.RowId, cutscenePath, cutbData,
            name => sheets.TryGetValue(name, out var sheet) ? sheet : null,
            language.Code);
        if (manifest.Lines.Count == 0) continue;

        var sheetByKey = sheets
            .SelectMany(sheet => sheet.Value.Keys.Select(key => (Key: key, Sheet: sheet.Key)))
            .GroupBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Sheet,
                StringComparer.OrdinalIgnoreCase);
        foreach (var line in manifest.Lines)
        {
            if (!line.IsVoiced && !line.IsPlayerChoice && IsIdentifiableActor(line.ActorToken))
                undubbedActorTokens.Add(line.ActorToken);
            if (line.IsVoiced && line.ScdPath is not null && !String.IsNullOrWhiteSpace(line.Text))
            {
                if (!actors.TryGetValue(line.ActorToken, out var byLanguage))
                    actors[line.ActorToken] = byLanguage = new(StringComparer.Ordinal);
                if (!byLanguage.TryGetValue(language.Name, out var sources))
                    byLanguage[language.Name] = sources = [];
                sources.Add(new(row.RowId, line.Key, line.ScdPath, line.Text));
            }
            if (!sheetByKey.TryGetValue(line.Key, out var sheetName))
                throw new InvalidDataException(
                    $"Cutscene {row.RowId}/{language.Name} line '{line.Key}' has no owning sheet");
            var normalizedActor = NormalizeActor(line.ActorToken);
            officialGroups.TryGetValue(normalizedActor, out var officialGroup);
            var identifiableActor = IsIdentifiableActor(line.ActorToken) ? normalizedActor : null;
            nodes.Add(new(
                row.RowId,
                language.Name,
                line.NodeId,
                HashSheetKey(sheetName, line.Key),
                line.Order,
                null,
                officialGroup,
                identifiableActor is null ? null : Sha256(identifiableActor),
                line.IsVoiced ? "native" : line.IsPlayerChoice ? "choice" : "synthetic"));
        }

        var groups = manifest.Lines.GroupBy(line => line.Ordinal).OrderBy(group => group.Key)
            .Select(group => group.ToArray()).ToArray();
        foreach (var start in groups[0])
            edges.Add(new(row.RowId, language.Name, null, start.NodeId));
        for (var index = 0; index + 1 < groups.Length; index++)
        foreach (var current in groups[index])
        foreach (var next in groups[index + 1])
            edges.Add(new(row.RowId, language.Name, current.NodeId, next.NodeId));
        foreach (var end in groups[^1])
            edges.Add(new(row.RowId, language.Name, end.NodeId, null));
    }
}

var plan = new ScenePlan(2, nodes, edges.Distinct().ToArray());
var output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(plan, JsonOptions(writeIndented: true)));
Console.WriteLine(
    $"cutscenes={parsedCutscenes} nodes={plan.Nodes.Count} edges={plan.Edges.Count} output={output}");
if (args.Length == 4)
{
    var enumeration = new EnumerationResult(
        1,
        parsedCutscenes,
        missingCutb,
        undubbedActorTokens.Order(StringComparer.Ordinal).ToArray(),
        actors.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new Actor(pair.Key,
                pair.Value.ToDictionary(
                    value => value.Key,
                    value => (IReadOnlyList<Source>)value.Value
                        .DistinctBy(source => source.ScdPath, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(source => source.CutsceneId)
                        .ThenBy(source => source.Key, StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.Ordinal)))
            .ToArray());
    var enumerationOutput = Path.GetFullPath(args[3]);
    Directory.CreateDirectory(Path.GetDirectoryName(enumerationOutput)!);
    File.WriteAllText(enumerationOutput,
        JsonSerializer.Serialize(enumeration, JsonOptions(writeIndented: true)));
    Console.WriteLine(
        $"actors={enumeration.Actors.Count} undubbed={enumeration.UndubbedActorTokens.Count} output={enumerationOutput}");
}

static Dictionary<string, IReadOnlyDictionary<string, string>> LoadSheets(
    GameData game, ReadOnlySpan<byte> cutb, Language language)
{
    var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(
        StringComparer.OrdinalIgnoreCase);
    foreach (var sheetName in CutsceneVoiceManifestParser.ExtractDialogueSheetNames(cutb))
    {
        try
        {
            var rows = game.GetExcelSheet<RawRow>(language, sheetName);
            if (rows is null) continue;
            result[sheetName] = rows
                .ToDictionary(row => row.ReadStringColumn(0).ToString(),
                    row => row.ReadStringColumn(1).ToString(), StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }
    return result;
}

static string HashSheetKey(string sheetName, string key) => Sha256(sheetName + "\0" + key);
static string Sha256(string value) => Convert.ToHexString(
    SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
static string NormalizeActor(string value) => new(value.Where(Char.IsLetterOrDigit)
    .Select(Char.ToLowerInvariant).ToArray());
static bool IsIdentifiableActor(string value) =>
    !String.IsNullOrWhiteSpace(value)
    && value is not "ALL" and not "SYSTEM" and not "NARRATION" and not "MEMORY"
        and not "NONE_VOICE"
    && !(value.Length > 1 && value[0] is 'Q' or 'A' && value[1..].All(Char.IsDigit));
static JsonSerializerOptions JsonOptions(bool writeIndented = false) => new()
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = writeIndented,
};

sealed record Catalog(IReadOnlyList<CatalogGroup> Groups);
sealed record CatalogGroup(string Id, IReadOnlyList<string> ActorTokens);
sealed record ScenePlan(int SchemaVersion, IReadOnlyList<SceneNode> Nodes,
    IReadOnlyList<SceneEdge> Edges);
sealed record SceneNode(uint CutsceneId, string Language, string NodeId,
    string SheetKeyHash, int Occurrence, uint? ActorNpcBaseId, string? OfficialGroupId,
    string? ActorTokenHash, string NodeKind);
sealed record SceneEdge(uint CutsceneId, string Language, string? CurrentNodeId,
    string? NextNodeId);
sealed record Source(uint CutsceneId, string Key, string ScdPath, string Transcript);
sealed record Actor(string ActorToken, IReadOnlyDictionary<string, IReadOnlyList<Source>> Languages);
sealed record EnumerationResult(int SchemaVersion, int ParsedCutscenes, int MissingCutb,
    IReadOnlyList<string> UndubbedActorTokens, IReadOnlyList<Actor> Actors);
