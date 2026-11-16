using System.Text.RegularExpressions;

namespace Resonance.Game;

internal static partial class DialogueText
{
    public static string NormalizeSpeaker(string value) => new(value
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant)
        .ToArray());

    public static string NormalizeText(string value) => String.Join(' ',
        SpeakerLabelPattern().Replace(value, String.Empty).Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"^(?:\(-[^)]*-\))+\s*")]
    private static partial Regex SpeakerLabelPattern();
}
