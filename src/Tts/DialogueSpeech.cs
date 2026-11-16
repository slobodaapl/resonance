using System.Text;
using Resonance.Audio;

namespace Resonance.Tts;

public abstract record DialogueAudioPart;
public sealed record SpeechPassage(string Text) : DialogueAudioPart;
public sealed record DialogueEffect(string Id, ReadOnlyMemory<float> Samples) : DialogueAudioPart;
public sealed record DialogueSpeechPlan(IReadOnlyList<DialogueAudioPart> Parts, bool HasCues,
    bool EffectsEnabled, string CacheIdentity)
{
    public bool HasSpeech => Parts.Any(part => part is SpeechPassage);
    public bool IsSilent => Parts.Count == 0;
    public double EffectDurationSeconds => Parts.OfType<DialogueEffect>().Sum(part => part.Samples.Length / 24000d);
}

public sealed class DialogueSpeech(DialogueSoundEffects effects)
{
    public static DialogueSpeech WithoutEffects { get; } = new(new DialogueSoundEffects(null));

    public DialogueSpeechPlan Prepare(string text, bool replaceEffects)
    {
        var matches = DialogueCues.Pattern().Matches(text);
        if (matches.Count == 0)
            return new(String.IsNullOrWhiteSpace(text) ? [] : [new SpeechPassage(text)], false, replaceEffects, "");
        var parts = new List<DialogueAudioPart>();
        var pending = new StringBuilder();
        var offset = 0;
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            pending.Append(text.AsSpan(offset, match.Index - offset));
            offset = match.Index + match.Length;
            if (!replaceEffects || !effects.TryGet(match.Value[1..^1], out var id, out var samples))
            {
                pending.Append(' ');
                continue;
            }
            FlushSpeech();
            if (parts.LastOrDefault() is not DialogueEffect previous || previous.Id != id)
                parts.Add(new DialogueEffect(id, samples));
        }
        pending.Append(text.AsSpan(offset));
        FlushSpeech();
        return new(parts, true, replaceEffects,
            replaceEffects ? "dialogue-cues-v1:on:" + effects.Identity : "dialogue-cues-v1:off:" + DialogueCues.Identity);

        void FlushSpeech()
        {
            var value = pending.ToString();
            pending.Clear();
            if (value.Any(char.IsLetterOrDigit))
                parts.Add(new SpeechPassage(String.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));
        }
    }

    public async Task SynthesizeAsync(DialogueSpeechPlan plan, StreamingAudioBuffer output,
        Func<string, StreamingAudioBuffer, CancellationToken, Task> synthesize, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (plan.Parts is [SpeechPassage only])
            {
                await synthesize(only.Text, output, cancellation.Token).ConfigureAwait(false);
                output.Complete();
                return;
            }
            foreach (var part in plan.Parts)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (part is DialogueEffect effect)
                {
                    for (var offset = 0; offset < effect.Samples.Length; offset += 4096)
                        if (!await output.WriteAsync(effect.Samples.Slice(offset,
                                Math.Min(4096, effect.Samples.Length - offset)), cancellation.Token).ConfigureAwait(false))
                            throw new OperationCanceledException(cancellation.Token);
                    continue;
                }
                var passage = (SpeechPassage)part;
                using var generated = new StreamingAudioBuffer();
                var production = ProduceAsync();
                try
                {
                    await foreach (var chunk in generated.Reader.ReadAllAsync(cancellation.Token).ConfigureAwait(false))
                    {
                        using (chunk)
                            if (!await output.WriteAsync(chunk.Samples, cancellation.Token).ConfigureAwait(false))
                                throw new OperationCanceledException(cancellation.Token);
                    }
                    await production.ConfigureAwait(false);
                }
                catch
                {
                    cancellation.Cancel();
                    generated.Invalidate();
                    try { await production.ConfigureAwait(false); } catch { }
                    throw;
                }

                async Task ProduceAsync()
                {
                    try
                    {
                        await synthesize(passage.Text, generated, cancellation.Token).ConfigureAwait(false);
                        generated.Complete();
                    }
                    catch (Exception error) { generated.Complete(error); throw; }
                }
            }
            output.Complete();
        }
        catch (Exception error) { output.Complete(error); throw; }
    }
}
