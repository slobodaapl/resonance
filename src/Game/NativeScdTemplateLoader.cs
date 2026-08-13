using Resonance.Audio;
using Resonance.Data;

namespace Resonance.Game;

public sealed class NativeScdTemplateLoader(Database database, ScdExtractor extractor)
{
    private const int CandidateLimit = 16;
    private NativeScdTemplate? cached;

    public async Task<NativeScdTemplate> LoadAsync(CancellationToken token)
    {
        if (cached is { } existing) return existing;

        var paths = await database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT DISTINCT scd_path
FROM official_reference_clip
WHERE scd_path IS NOT NULL AND scd_path <> ''
ORDER BY validated_utc IS NULL, validated_utc DESC, created_utc DESC
LIMIT $limit
""";
            command.Parameters.AddWithValue("$limit", CandidateLimit);
            var result = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                result.Add(NormalizeGamePath(reader.GetString(0)));
            return result;
        }, token).ConfigureAwait(false);

        Exception? lastFailure = null;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var bytes = await extractor.CaptureResourceBytesAsync(path, token, logFailure: false)
                    .ConfigureAwait(false);
                if (!ScdFileBuilder.IsNativeTemplateCompatible(bytes)) continue;
                return cached = new NativeScdTemplate(path, bytes);
            }
            catch (FileNotFoundException error)
            {
                lastFailure = error;
            }
            catch (InvalidDataException error)
            {
                lastFailure = error;
            }
        }

        throw new FileNotFoundException(
            paths.Count == 0
                ? "No persisted official SCD source is available for game-mixer playback"
                : $"No compatible installed SCD resource was found among {paths.Count} persisted official sources",
            lastFailure);
    }

    internal static string NormalizeGamePath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
