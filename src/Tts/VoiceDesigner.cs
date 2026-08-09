using Resonance.Audio;

namespace Resonance.Tts;

public sealed class VoiceDesigner : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<string, string> ReferenceTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "Beyond the quiet harbor, bright lanterns shimmer while travelers exchange curious stories of distant roads.",
        ["japanese"] = "静かな港の向こうで明るい灯籠が揺れ、旅人たちは遠い道の不思議な物語を語り合う。",
        ["german"] = "Jenseits des stillen Hafens schimmern helle Laternen, während Reisende neugierige Geschichten ferner Wege erzählen.",
        ["french"] = "Au-delà du port tranquille, de vives lanternes scintillent tandis que les voyageurs racontent d'étranges histoires de routes lointaines.",
    };

    private readonly ITtsRuntime baseRuntime;
    private readonly string designPath;
    private readonly string codecPath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private QwenCppRuntime? designRuntime;
    private string backendName;

    public VoiceDesigner(ITtsRuntime baseRuntime, string designPath, string codecPath, string backendName)
    {
        this.baseRuntime = baseRuntime;
        this.designPath = designPath;
        this.codecPath = codecPath;
        this.backendName = backendName;
        designRuntime = new QwenCppRuntime(designPath, codecPath, backendName);
    }

    public async Task<VoiceReference> DesignReferenceAsync(string instruction, long seed, string language, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var active = designRuntime ?? throw new ObjectDisposedException(nameof(VoiceDesigner));
        using var buffer = new StreamingAudioBuffer();
        var referenceText = ReferenceText(language);
        var synthesis = active.SynthesizeAsync(
            new(referenceText, language, null, instruction, seed), buffer, token);
        var samples = new List<float>(24000 * 8);
        await foreach (var chunk in buffer.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            samples.AddRange(chunk.Samples.Span);
            chunk.Dispose();
        }
        await synthesis.ConfigureAwait(false);
        return await baseRuntime.ExtractReferenceAsync(samples.ToArray(), referenceText, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<VoiceReference> SynthesizeDesignedLineAsync(string text, string instruction, long seed, string language,
        StreamingAudioBuffer output, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var active = designRuntime ?? throw new ObjectDisposedException(nameof(VoiceDesigner));
            using var generated = new StreamingAudioBuffer();
            var synthesis = active.SynthesizeAsync(new(text, language, null, instruction, seed), generated, token);
            var samples = new List<float>(24000 * 8);
            try
            {
                await foreach (var chunk in generated.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    samples.AddRange(chunk.Samples.Span);
                    if (!output.TryWrite(chunk.Samples.Span)) throw new OperationCanceledException(token);
                    chunk.Dispose();
                }
                await synthesis.ConfigureAwait(false);
                output.Complete();
            }
            catch (Exception error)
            {
                output.Complete(error);
                throw;
            }
            return await baseRuntime.ExtractReferenceAsync(samples.ToArray(), text, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static string ReferenceText(string language) => ReferenceTexts.TryGetValue(language, out var text)
        ? text
        : throw new NotSupportedException($"FFXIV dubbing language '{language}' is not supported");

    public async Task SwitchBackendAsync(string backendName, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (String.Equals(this.backendName, backendName, StringComparison.Ordinal)) return;
            var replacement = new QwenCppRuntime(designPath, codecPath, backendName);
            var previous = designRuntime;
            designRuntime = replacement;
            this.backendName = backendName;
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (designRuntime is not null) await designRuntime.DisposeAsync().ConfigureAwait(false);
            designRuntime = null;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }
}
