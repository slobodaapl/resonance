using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;
using Resonance.Tts;

namespace Resonance.Audio;

public sealed class DialogueSoundEffects
{
    private sealed record Library(Dictionary<string, float[]> Clips, string Identity);
    private readonly Lazy<Library> library;

    public DialogueSoundEffects(string? directory, Action<string, Exception>? reportError = null)
        => library = new(() => Load(directory, reportError));

    public string Identity => library.Value.Identity;

    public bool TryGet(string cue, out string id, out ReadOnlyMemory<float> samples)
    {
        samples = default;
        id = string.Empty;
        if (DialogueCues.Aliases.TryGetValue(DialogueCues.CueName(cue), out var mapped) && mapped is not null
            && library.Value.Clips.TryGetValue(mapped, out var pcm))
        {
            id = mapped;
            samples = pcm;
            return true;
        }
        return false;
    }

    private static Library Load(string? directory, Action<string, Exception>? reportError)
    {
        var clips = new Dictionary<string, float[]>(StringComparer.Ordinal);
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        identity.AppendData(Encoding.UTF8.GetBytes(DialogueCues.Identity));
        foreach (var id in DialogueCues.Aliases.Values.OfType<string>().Distinct().Order(StringComparer.Ordinal))
        {
            identity.AppendData(Encoding.UTF8.GetBytes(id + "\0"));
            if (directory is null) continue;
            try
            {
                var bytes = File.ReadAllBytes(Path.Combine(directory, id + ".wav"));
                using var stream = new MemoryStream(bytes, false);
                using var reader = new WaveFileReader(stream);
                if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || reader.WaveFormat.BitsPerSample != 16
                    || reader.WaveFormat.SampleRate != 24000 || reader.WaveFormat.Channels != 1
                    || reader.Length == 0 || reader.Length % sizeof(short) != 0)
                    throw new InvalidDataException("Dialogue effects must be nonempty 24 kHz mono PCM16 WAVs");
                var samples = new float[checked((int)(reader.Length / sizeof(short)))];
                using var pcm = new BinaryReader(reader, Encoding.UTF8, leaveOpen: true);
                for (var index = 0; index < samples.Length; index++) samples[index] = pcm.ReadInt16() / 32768f;
                clips.Add(id, samples);
                identity.AppendData(bytes);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidDataException or FormatException or OverflowException)
            {
                identity.AppendData("missing"u8);
                reportError?.Invoke(id, error);
            }
        }
        return new(clips, Convert.ToHexStringLower(identity.GetHashAndReset()));
    }
}
