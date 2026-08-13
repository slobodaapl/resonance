using Resonance.Audio;
using Resonance.Plugin;

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

    private readonly Func<ReadOnlyMemory<float>, string, CancellationToken, ValueTask<VoiceReference>> extractReference;
    private readonly string designPath;
    private readonly string codecPath;
    private readonly IProcessLifetimeLease pluginLifetimeLease;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object disposeGate = new();
    private QwenCppRuntime? designRuntime;
    private string backendName;
    private int switching;
    private int runtimeDisposalFailed;
    private int disposed;
    private Task? disposeTask;

    public bool IsSwitching => Volatile.Read(ref switching) != 0;
    public bool IsReady => designRuntime is not null && !IsSwitching
                           && Volatile.Read(ref runtimeDisposalFailed) == 0;
    public string BackendName => Volatile.Read(ref backendName);

    internal VoiceDesigner(ITtsRuntime baseRuntime, string designPath, string codecPath, string backendName,
        IProcessLifetimeLease pluginLifetimeLease,
        Func<ReadOnlyMemory<float>, string, CancellationToken, ValueTask<VoiceReference>>? extractReference = null)
    {
        this.pluginLifetimeLease = pluginLifetimeLease
            ?? throw new ArgumentNullException(nameof(pluginLifetimeLease));
        this.extractReference = extractReference ?? baseRuntime.ExtractReferenceAsync;
        this.designPath = designPath;
        this.codecPath = codecPath;
        this.backendName = backendName;
        // VoiceDesign is a separate model. It may remain alive while the
        // Base context is released for the out-of-process reference helper.
        designRuntime = new QwenCppRuntime(designPath, codecPath, backendName,
            ownsProcessLease: false, pluginLifetimeLease: pluginLifetimeLease);
    }

    public async Task<VoiceReference> DesignReferenceAsync(string instruction, long seed, string language, CancellationToken token)
    {
        ThrowIfDisposed();
        await gate.WaitAsync(token).ConfigureAwait(false);
        Task? synthesis = null;
        try
        {
            var active = designRuntime ?? throw new ObjectDisposedException(nameof(VoiceDesigner));
            using var buffer = new StreamingAudioBuffer();
            var referenceText = ReferenceText(language);
            synthesis = active.SynthesizeAsync(
                new(referenceText, language, null, instruction, seed), buffer, token);
            var samples = new List<float>(24000 * 8);
            await foreach (var chunk in buffer.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                samples.AddRange(chunk.Samples.Span);
                chunk.Dispose();
            }
            await synthesis.ConfigureAwait(false);
            return await extractReference(samples.ToArray(), referenceText, token).ConfigureAwait(false);
        }
        finally
        {
            if (synthesis is not null) await ObserveAsync(synthesis).ConfigureAwait(false);
            gate.Release();
        }
    }

    public async Task<VoiceReference> SynthesizeDesignedLineAsync(string text, string instruction, long seed, string language,
        StreamingAudioBuffer output, CancellationToken token)
    {
        ThrowIfDisposed();
        return await SynthesizeDesignedLineCoreAsync(text, instruction, seed, language, output, token, true)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("VoiceDesign did not produce a reference");
    }

    public async Task SynthesizeDesignedLineOnlyAsync(string text, string instruction, long seed, string language,
        StreamingAudioBuffer output, CancellationToken token)
    {
        ThrowIfDisposed();
        await SynthesizeDesignedLineCoreAsync(text, instruction, seed, language, output, token, false)
            .ConfigureAwait(false);
    }

    private async Task<VoiceReference?> SynthesizeDesignedLineCoreAsync(string text, string instruction, long seed,
        string language, StreamingAudioBuffer output, CancellationToken token, bool extractReferenceVoice)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        Task? synthesis = null;
        try
        {
            var active = designRuntime ?? throw new ObjectDisposedException(nameof(VoiceDesigner));
            using var generated = new StreamingAudioBuffer();
            synthesis = active.SynthesizeAsync(new(text, language, null, instruction, seed), generated, token);
            List<float>? samples = extractReferenceVoice ? new List<float>(24000 * 8) : null;
            try
            {
                await foreach (var chunk in generated.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    samples?.AddRange(chunk.Samples.Span);
                    if (!await output.WriteAsync(chunk.Samples, token).ConfigureAwait(false))
                        throw new OperationCanceledException(token);
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
            if (!extractReferenceVoice) return null;
            return await extractReference(samples!.ToArray(), text, token).ConfigureAwait(false);
        }
        finally
        {
            if (synthesis is not null) await ObserveAsync(synthesis).ConfigureAwait(false);
            gate.Release();
        }
    }

    private static string ReferenceText(string language) => ReferenceTexts.TryGetValue(language, out var text)
        ? text
        : throw new NotSupportedException($"FFXIV dubbing language '{language}' is not supported");

    public async Task SwitchBackendAsync(string backendName, CancellationToken token)
    {
        ThrowIfDisposed();
        Interlocked.Exchange(ref switching, 1);
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (designRuntime is not null
                    && String.Equals(this.backendName, backendName, StringComparison.Ordinal)) return;
                if (Volatile.Read(ref runtimeDisposalFailed) != 0)
                    throw new InvalidOperationException(
                        "The previous VoiceDesign runtime did not dispose safely; restart is required before switching backends");
                var previous = designRuntime;
                var previousBackend = this.backendName;
                if (previous is not null)
                {
                    try { await previous.DisposeAsync().ConfigureAwait(false); }
                    catch
                    {
                        Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                        throw;
                    }
                }
                // Keep the previous context published until disposal has
                // succeeded.  A failed native drain must not be followed by
                // a replacement allocation or leave callers with a false
                // ready state and no owner reference.
                designRuntime = null;
                try
                {
                    var replacement = new QwenCppRuntime(designPath, codecPath, backendName,
                        ownsProcessLease: false, pluginLifetimeLease: pluginLifetimeLease);
                    designRuntime = replacement;
                    this.backendName = backendName;
                    Interlocked.Exchange(ref runtimeDisposalFailed, 0);
                }
                catch (Exception migrationError)
                {
                    try
                    {
                        designRuntime = new QwenCppRuntime(designPath, codecPath, previousBackend,
                            ownsProcessLease: false, pluginLifetimeLease: pluginLifetimeLease);
                    }
                    catch (Exception restorationError)
                    {
                        designRuntime = null;
                        throw new AggregateException(
                            "VoiceDesign backend migration and previous-backend restoration both failed",
                            migrationError, restorationError);
                    }
                    throw;
                }
            }
            finally
            {
                gate.Release();
            }
        }
        finally { Interlocked.Exchange(ref switching, 0); }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            if (disposeTask is not null) return new ValueTask(disposeTask);
            Interlocked.Exchange(ref disposed, 1);
            disposeTask = DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = designRuntime;
            if (previous is not null)
            {
                try { await previous.DisposeAsync().ConfigureAwait(false); }
                catch
                {
                    Interlocked.Exchange(ref runtimeDisposalFailed, 1);
                    throw;
                }
            }
            designRuntime = null;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private static async Task ObserveAsync(Task synthesis)
    {
        try { await synthesis.ConfigureAwait(false); }
        catch { }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
            throw new ObjectDisposedException(nameof(VoiceDesigner));
    }
}
