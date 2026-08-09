namespace Resonance.Plugin;

/// <summary>Strict parsing boundary for v1 territory/archetype override keys.</summary>
public static class LegacyCastingMigration
{
    public static bool TryParseKey(string key, out uint territoryId, out string sex)
    {
        territoryId = 0;
        sex = string.Empty;
        var separator = key.IndexOf(':');
        if (separator <= 0 || !uint.TryParse(key[..separator], out territoryId)) return false;

        var archetype = key[(separator + 1)..].Trim().ToLowerInvariant();
        if (archetype == "feminine" || HasExplicitVariant(archetype, "feminine"))
        {
            sex = "feminine";
            return true;
        }
        if (archetype == "masculine" || HasExplicitVariant(archetype, "masculine"))
        {
            sex = "masculine";
            return true;
        }
        return false;
    }

    private static bool HasExplicitVariant(string archetype, string sex) =>
        archetype.StartsWith($"{sex}_", StringComparison.Ordinal)
        && archetype.Length > sex.Length + 1;
}
