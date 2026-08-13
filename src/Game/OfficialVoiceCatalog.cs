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
    IReadOnlyDictionary<string, IReadOnlyList<OfficialVoiceSource>> Sources,
    IReadOnlyList<string>? ActorTokens = null)
{
    public IReadOnlyList<string> ExactActorTokens => ActorTokens ?? [];
}

public sealed class OfficialVoiceCatalog
{
    private static readonly HashSet<string> Languages =
        ["english", "japanese", "german", "french"];
    private readonly Dictionary<uint, OfficialVoiceGroup> byNpcBaseId;
    private readonly Dictionary<(string Language, string Alias), OfficialVoiceGroup> byAlias;
    private readonly Dictionary<(string Language, string Token), OfficialVoiceGroup> byActorToken;
    private readonly Dictionary<string, OfficialVoiceGroup> byExactActorToken;

    public int Version { get; }
    public IReadOnlyList<OfficialVoiceGroup> Groups { get; }

    private OfficialVoiceCatalog(int version, IReadOnlyList<OfficialVoiceGroup> groups)
    {
        Version = version;
        Groups = groups;
        byNpcBaseId = [];
        byAlias = [];
        byActorToken = [];
        byExactActorToken = [];
        foreach (var group in groups)
        {
            foreach (var id in group.NpcBaseIds) byNpcBaseId.Add(id, group);
            foreach (var actorToken in group.ExactActorTokens)
                byExactActorToken.Add(NormalizeActorToken(actorToken), group);
            foreach (var (language, aliases) in group.Aliases)
                foreach (var alias in aliases)
                {
                    byAlias.Add((language, NormalizeAlias(alias)), group);
                    var tokenKey = (language, NormalizeActorToken(alias));
                    if (byActorToken.TryGetValue(tokenKey, out var existing)
                        && !ReferenceEquals(existing, group))
                        throw new InvalidDataException(
                            $"Official voice aliases collapse to ambiguous actor token '{tokenKey.Item2}'");
                    byActorToken[tokenKey] = group;
                }
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
        var actorTokens = new HashSet<string>(StringComparer.Ordinal);
        var resources = new HashSet<(string, string, uint)>();
        foreach (var group in groups)
        {
            if (group is null || String.IsNullOrWhiteSpace(group.Id) || String.IsNullOrWhiteSpace(group.Label)
                || !ids.Add(group.Id))
                throw new InvalidDataException("Official voice catalog contains a null, blank, or duplicate group");
            if (group.NpcBaseIds is null || group.Aliases is null || group.Sources is null)
                throw new InvalidDataException($"Official voice group '{group.Id}' has a missing collection");
            foreach (var actorToken in group.ExactActorTokens)
                if (String.IsNullOrWhiteSpace(actorToken)
                    || !actorTokens.Add(NormalizeActorToken(actorToken)))
                    throw new InvalidDataException(
                        $"Official voice group '{group.Id}' has a blank or ambiguous actor token");
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
        var normalizedLanguage = NormalizeLanguage(language);
        return byExactActorToken.GetValueOrDefault(NormalizeActorToken(displayName))
               ?? byAlias.GetValueOrDefault((normalizedLanguage, NormalizeAlias(displayName)))
               ?? byActorToken.GetValueOrDefault((normalizedLanguage, NormalizeActorToken(displayName)));
    }

    public OfficialVoiceGroup? GetGroup(string groupId) =>
        Groups.FirstOrDefault(group => String.Equals(group.Id, groupId, StringComparison.Ordinal));

    public static string CanonicalSpeakerKey(string groupId) => $"official:{groupId}";

    private static void ValidateLanguages(string groupId, IEnumerable<string> values)
    {
        foreach (var language in values)
            if (!Languages.Contains(language))
                throw new InvalidDataException($"Official voice group '{groupId}' uses unsupported language '{language}'");
    }

    private static string NormalizeAlias(string value) => String.Join(' ', value.Split(
        (char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string NormalizeActorToken(string value) => new(value
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant)
        .ToArray());

    private static string NormalizeLanguage(string value) => value.Trim().ToLowerInvariant();

    private sealed record Root(int SchemaVersion, int CatalogVersion, List<OfficialVoiceGroup?>? Groups);
}
