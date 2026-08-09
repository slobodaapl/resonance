using System.Text.Json;
using System.Text.Json.Serialization;

namespace Resonance.Game;

public sealed record OfficialVoiceSource(
    string ScdPath,
    uint SoundNumber,
    string Transcript,
    bool Preferred = false);

public sealed record OfficialVoiceGroup(
    string Id,
    string Label,
    IReadOnlyList<uint> NpcBaseIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Aliases,
    IReadOnlyDictionary<string, IReadOnlyList<OfficialVoiceSource>> Sources);

public sealed class OfficialVoiceCatalog
{
    private static readonly HashSet<string> Languages =
        ["english", "japanese", "german", "french"];
    private readonly Dictionary<uint, OfficialVoiceGroup> byNpcBaseId;
    private readonly Dictionary<(string Language, string Alias), OfficialVoiceGroup> byAlias;

    public int Version { get; }
    public IReadOnlyList<OfficialVoiceGroup> Groups { get; }

    private OfficialVoiceCatalog(int version, IReadOnlyList<OfficialVoiceGroup> groups)
    {
        Version = version;
        Groups = groups;
        byNpcBaseId = [];
        byAlias = [];
        foreach (var group in groups)
        {
            foreach (var id in group.NpcBaseIds) byNpcBaseId.Add(id, group);
            foreach (var (language, aliases) in group.Aliases)
                foreach (var alias in aliases)
                    byAlias.Add((language, NormalizeAlias(alias)), group);
        }
    }

    public static OfficialVoiceCatalog Load(string path) => Parse(File.ReadAllText(path));

    public static OfficialVoiceCatalog Parse(string json)
    {
        var root = JsonSerializer.Deserialize<Root>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        }) ?? throw new InvalidDataException("Official voice catalog is empty");
        if (root.SchemaVersion != 1 || root.CatalogVersion <= 0)
            throw new InvalidDataException("Official voice catalog schema or version is invalid");
        var groups = root.Groups ?? throw new InvalidDataException("Official voice catalog groups are missing");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var npcIds = new HashSet<uint>();
        var aliases = new HashSet<(string, string)>();
        var resources = new HashSet<(string, string, uint)>();
        foreach (var group in groups)
        {
            if (group is null || String.IsNullOrWhiteSpace(group.Id) || String.IsNullOrWhiteSpace(group.Label)
                || !ids.Add(group.Id))
                throw new InvalidDataException("Official voice catalog contains a null, blank, or duplicate group");
            if (group.NpcBaseIds is null || group.Aliases is null || group.Sources is null)
                throw new InvalidDataException($"Official voice group '{group.Id}' has a missing collection");
            foreach (var npcId in group.NpcBaseIds)
                if (npcId == 0 || !npcIds.Add(npcId))
                    throw new InvalidDataException($"Official voice group '{group.Id}' has an invalid or duplicate NpcBaseId");
            ValidateLanguages(group.Id, group.Aliases?.Keys ?? []);
            ValidateLanguages(group.Id, group.Sources?.Keys ?? []);
            foreach (var (language, values) in group.Aliases
                         ?? new Dictionary<string, IReadOnlyList<string>>())
                foreach (var alias in values ?? [])
                    if (String.IsNullOrWhiteSpace(alias) || !aliases.Add((language, NormalizeAlias(alias))))
                        throw new InvalidDataException($"Official voice group '{group.Id}' has a blank or ambiguous alias");
            foreach (var (language, values) in group.Sources
                         ?? new Dictionary<string, IReadOnlyList<OfficialVoiceSource>>())
                foreach (var source in values ?? [])
                {
                    if (source is null || String.IsNullOrWhiteSpace(source.Transcript)
                        || String.IsNullOrWhiteSpace(source.ScdPath)
                        || !source.ScdPath.StartsWith("cut/", StringComparison.OrdinalIgnoreCase)
                        || !source.ScdPath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)
                        || source.ScdPath.Contains("..", StringComparison.Ordinal)
                        || !resources.Add((language, source.ScdPath.ToLowerInvariant(), source.SoundNumber)))
                        throw new InvalidDataException($"Official voice group '{group.Id}' has an invalid or duplicate source");
                }
        }
        return new(root.CatalogVersion, groups!);
    }

    public OfficialVoiceGroup? Resolve(uint? npcBaseId, string displayName, string language)
    {
        if (npcBaseId is { } id && byNpcBaseId.TryGetValue(id, out var exact)) return exact;
        return byAlias.GetValueOrDefault((NormalizeLanguage(language), NormalizeAlias(displayName)));
    }

    public static string CanonicalSpeakerKey(string groupId) => $"official:{groupId}";

    private static void ValidateLanguages(string groupId, IEnumerable<string> values)
    {
        foreach (var language in values)
            if (!Languages.Contains(language))
                throw new InvalidDataException($"Official voice group '{groupId}' uses unsupported language '{language}'");
    }

    private static string NormalizeAlias(string value) => String.Join(' ', value.Split(
        (char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string NormalizeLanguage(string value) => value.Trim().ToLowerInvariant();

    private sealed record Root(int SchemaVersion, int CatalogVersion, List<OfficialVoiceGroup?>? Groups);
}
