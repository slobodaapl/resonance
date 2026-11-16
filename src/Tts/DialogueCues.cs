using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Resonance.Tts;

public static class DialogueCues
{
    private sealed record Catalog(Dictionary<string, string[]> Effects, string[] Silent);
    private sealed record Rules(Dictionary<string, string?> Aliases, Regex Pattern, string Identity);
    private static readonly Lazy<Rules> rules = new(Load);

    internal static IReadOnlyDictionary<string, string?> Aliases => rules.Value.Aliases;
    public static string Identity => rules.Value.Identity;
    public static Regex Pattern() => rules.Value.Pattern;

    internal static string CueName(string text) => text.Trim().TrimEnd('.', '…').TrimEnd();

    private static Rules Load()
    {
        using var resource = typeof(DialogueCues).Assembly.GetManifestResourceStream("Resonance.DialogueCues.json")!;
        using var content = new MemoryStream();
        resource.CopyTo(content);
        var bytes = content.ToArray();
        var catalog = JsonSerializer.Deserialize<Catalog>(bytes, new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true })!;
        var aliases = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (effect, names) in catalog.Effects)
            foreach (var name in names) aliases.Add(CueName(name), effect);
        foreach (var name in catalog.Silent) aliases.Add(CueName(name), null);
        var namesPattern = string.Join('|', aliases.Keys.OrderByDescending(name => name.Length).Select(Regex.Escape));
        var cue = @"\s*(?:" + namesPattern + @")\s*[.…]*\s*";
        var pattern = new Regex(@"<[^<>]*>|\(" + cue + @"\)|（" + cue + @"）|\*" + cue + @"\*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return new(aliases, pattern, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public static bool Contains(string text) => Pattern().IsMatch(text);

    public static string SpeechText(string text)
    {
        if (!Contains(text)) return text;
        var stripped = Pattern().Replace(text, " ");
        return stripped.Any(char.IsLetterOrDigit) ? stripped : string.Empty;
    }

    public static bool HasSpeech(string text) => !String.IsNullOrWhiteSpace(SpeechText(text));
}
