using System.Security.Cryptography;
using System.Text;

namespace Resonance.Data;

public sealed class NativeVoiceRepository(Database database)
{
    public Task RecordAsync(long speakerId, string scdPath, uint soundNumber, string transcript, CancellationToken token) =>
        database.WriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO native_voice_observation(
                  speaker_id,scd_path_hash,sound_number,transcript_hash,observed_utc)
                VALUES($speaker,$path,$sound,$transcript,$utc)
                """;
            command.Parameters.AddWithValue("$speaker", speakerId);
            command.Parameters.AddWithValue("$path", Hash(scdPath.ToLowerInvariant()));
            command.Parameters.AddWithValue("$sound", soundNumber);
            command.Parameters.AddWithValue("$transcript", Hash(Normalize(transcript)));
            command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, token);

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
