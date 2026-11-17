using System.Text.Json;
using NAudio.Wave;
using Resonance.Audio;
using Resonance.Bootstrap;
using Resonance.Data;
using Resonance.Plugin;
using Resonance.Scheduling;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class DialogueSoundEffectsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("<sigh>", "sigh")]
    [InlineData("<seufz>", "sigh")]
    [InlineData("(soupire)", "sigh")]
    [InlineData("（ため息）", "sigh")]
    [InlineData("*sigh*", "sigh")]
    [InlineData("<klack>", "click-clack")]
    [InlineData("<biep>", "blip")]
    [InlineData("<hust>", "cough")]
    [InlineData("(tousse)", "cough")]
    [InlineData("(snif)", "sniff")]
    [InlineData("<schluchz>", "sob")]
    [InlineData("(Sanglot)", "sob")]
    [InlineData("（泣き）", "sob")]
    [InlineData("(halète)", "pant")]
    [InlineData("<hicks>", "hic")]
    [InlineData("(gémit)", "groan")]
    [InlineData("(surprise)", "gasp")]
    public void ExplicitLocalizedCuesUseBundledEffects(string marker, string expected)
    {
        var speech = new DialogueSpeech(new DialogueSoundEffects(BundledEffects));
        var plan = speech.Prepare(marker, true);
        var effect = Assert.IsType<DialogueEffect>(Assert.Single(plan.Parts));
        Assert.Equal(expected, effect.Id);
        Assert.False(effect.Samples.IsEmpty);
        Assert.False(plan.HasSpeech);
        Assert.Empty(DialogueCues.SpeechText(marker));
    }

    [Theory]
    [InlineData("ピピ… ハァ… ゲホ…")]
    [InlineData("Bip... Bip... Je suis là (Encore merci pour tout...).")]
    [InlineData("Il est ici (La situation est critique... Oui, dépêchez-vous.).")]
    [InlineData("（こくえのもり） （自分にはできないと首を横に振る）")]
    [InlineData("We should (probably) keep moving. *Important*.")]
    public void OrdinaryTextAndParentheticalDialogueStayUnchanged(string text)
    {
        var plan = DialogueSpeech.WithoutEffects.Prepare(text, true);
        Assert.Equal(text, Assert.IsType<SpeechPassage>(Assert.Single(plan.Parts)).Text);
        Assert.Equal(text, DialogueCues.SpeechText(text));
        Assert.False(plan.HasCues);
        Assert.Empty(plan.CacheIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownCuesAreNeverSpokenAndUnsupportedDirectionsAreSilent(bool enabled)
    {
        var plan = DialogueSpeech.WithoutEffects.Prepare("Before <unknown cue> (petit rire) after.", enabled);
        Assert.Equal("Before after.", Assert.IsType<SpeechPassage>(Assert.Single(plan.Parts)).Text);
        Assert.True(DialogueSpeech.WithoutEffects.Prepare("<unknown cue> ... (petit rire)", enabled).IsSilent);
    }

    [Fact]
    public void RepeatedAliasesCollapseOnlyUntilSpeechOrAnotherEffectIntervenes()
    {
        using var fixture = new EffectsFixture();
        var plan = fixture.Speech.Prepare("Start <click>...<clack> <unknown> <klick> end <click><sigh><click>", true);
        Assert.Collection(plan.Parts,
            part => Assert.Equal("Start", Assert.IsType<SpeechPassage>(part).Text),
            part => Assert.Equal("click-clack", Assert.IsType<DialogueEffect>(part).Id),
            part => Assert.Equal("end", Assert.IsType<SpeechPassage>(part).Text),
            part => Assert.Equal("click-clack", Assert.IsType<DialogueEffect>(part).Id),
            part => Assert.Equal("sigh", Assert.IsType<DialogueEffect>(part).Id),
            part => Assert.Equal("click-clack", Assert.IsType<DialogueEffect>(part).Id));
        var disabled = fixture.Speech.Prepare("Start<sigh>middle (soupire)... end", false);
        Assert.Equal("Start middle ... end", Assert.IsType<SpeechPassage>(Assert.Single(disabled.Parts)).Text);
        Assert.NotEqual(plan.CacheIdentity, disabled.CacheIdentity);
    }

    [Fact]
    public void NestedOrdinaryAsideDoesNotHideAnExplicitCue()
    {
        var text = "Before (an aside (soupire) continues) after";
        Assert.Equal("Before (an aside   continues) after", DialogueCues.SpeechText(text));
    }

    [Fact]
    public async Task MixedLineStreamsInOrderAndCompletesOnlyAfterFinalPassage()
    {
        using var fixture = new EffectsFixture();
        var plan = fixture.Speech.Prepare("Before <sigh> after", true);
        using var output = new StreamingAudioBuffer();
        var releaseFirst = Signal();
        var releaseLast = Signal();
        var lastStarted = Signal();
        var calls = new List<string>();
        var generation = fixture.Speech.SynthesizeAsync(plan, output, async (text, sink, token) =>
        {
            calls.Add(text);
            Assert.True(await sink.WriteAsync(new float[] { text == "Before" ? .75f : -.75f }, token));
            if (text == "Before") await releaseFirst.Task.WaitAsync(token);
            else { lastStarted.SetResult(); await releaseLast.Task.WaitAsync(token); }
            sink.Complete();
        }, Token);
        using (var first = await output.Reader.ReadAsync(Token).AsTask().WaitAsync(Timeout, Token))
            Assert.Equal(new float[] { .75f }, first.Samples.ToArray());
        Assert.False(output.ProducerCompleted);
        releaseFirst.SetResult();
        await lastStarted.Task.WaitAsync(Timeout, Token);
        Assert.False(output.ProducerCompleted);
        releaseLast.SetResult();
        await generation.WaitAsync(Timeout, Token);
        Assert.Equal(new float[] { .25f, .5f, -.75f }, await output.DrainAsync(Token));
        Assert.Equal(new[] { "Before", "after" }, calls);
        Assert.True(output.ProducerCompleted);
    }

    [Fact]
    public async Task CancellationObservesActiveProducerAndNeverStartsFollowingPassages()
    {
        using var fixture = new EffectsFixture();
        using var output = new StreamingAudioBuffer();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var started = Signal();
        var exited = Signal();
        var calls = 0;
        var generation = fixture.Speech.SynthesizeAsync(fixture.Speech.Prepare("Before <sigh> after", true),
            output, async (_, _, token) =>
            {
                Interlocked.Increment(ref calls);
                started.SetResult();
                try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token); }
                finally { exited.SetResult(); }
            }, cancellation.Token);
        await started.Task.WaitAsync(Timeout, Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation.WaitAsync(Timeout, Token));
        Assert.True(exited.Task.IsCompleted);
        Assert.Equal(1, calls);
        Assert.True(output.ProducerCompleted);
        Assert.Equal(0, output.TotalSamplesWritten);
    }

    [Fact]
    public async Task ProducerFailureTerminatesCompositeWithOriginalFailure()
    {
        using var fixture = new EffectsFixture();
        using var output = new StreamingAudioBuffer();
        var error = new InvalidDataException("inference failed");
        var calls = 0;
        var generation = fixture.Speech.SynthesizeAsync(fixture.Speech.Prepare("Before <sigh> after", true),
            output, (_, _, _) => { calls++; return Task.FromException(error); }, Token);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidDataException>(() => generation));
        Assert.Same(error, await Assert.ThrowsAsync<InvalidDataException>(() => output.DrainAsync(Token)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InvalidatingOutputUnblocksBackpressureAndCancelsProducer()
    {
        using var fixture = new EffectsFixture();
        using var output = new StreamingAudioBuffer(maxBufferedBytes: sizeof(float));
        var exited = Signal();
        var generation = fixture.Speech.SynthesizeAsync(fixture.Speech.Prepare("Before <sigh> after", true),
            output, async (_, sink, token) =>
            {
                Assert.True(await sink.WriteAsync(new float[] { .75f }, token));
                Assert.True(await sink.WriteAsync(new float[] { .5f }, token));
                try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token); }
                finally { exited.TrySetResult(); }
            }, Token);
        Assert.True(await output.Reader.WaitToReadAsync(Token).AsTask().WaitAsync(Timeout, Token));
        output.Invalidate();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation.WaitAsync(Timeout, Token));
        Assert.True(exited.Task.IsCompleted);
    }

    [Fact]
    public async Task CueOnlyHostRequestCompletesWithoutStartingUnavailableModel()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "absent-cue-runtime");
        await using var host = new BaseRuntimeHostClient(Path.Combine(root, "missing.exe"),
            Path.Combine(root, "talker"), Path.Combine(root, "codec"), root, root, root);
        using var output = new StreamingAudioBuffer();
        await host.SynthesizeAsync(new("<unknown> (soupire)", "english", null, null, 1), output, Token);
        Assert.True(output.ProducerCompleted);
        Assert.Empty(await output.DrainAsync(Token));
        Assert.False(host.IsReady);
    }

    [Fact]
    public void MissingOrInvalidEffectsAreSilentAndDoNotPreventSpokenPassages()
    {
        using var fixture = new EffectsFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "sigh.wav"), "version https://git-lfs.github.com/spec/v1");
        var failures = new List<string>();
        var speech = new DialogueSpeech(new DialogueSoundEffects(fixture.Root, (id, _) => failures.Add(id)));
        var plan = speech.Prepare("Before <sigh> after <gasp>", true);
        Assert.Equal("Before after", Assert.IsType<SpeechPassage>(Assert.Single(plan.Parts)).Text);
        Assert.Contains("sigh", failures);
        Assert.Contains("gasp", failures);
    }

    [Fact]
    public async Task EffectOnlyAndSilentLinesNeverResolveVoiceOrCallModel()
    {
        using var fixture = new EffectsFixture();
        using var database = new Database(Path.Combine(fixture.Root, "cache.sqlite3"));
        var cache = new LineCache(database, Path.Combine(fixture.Root, "cache"), () => 0);
        var runtime = new RecordingRuntime();
        await using var scheduler = new DubScheduler(runtime,
            (_, _) => throw new InvalidOperationException("No voice available"), cache, "model", "english",
            dialogueSpeech: fixture.Speech);
        var processed = new Dictionary<long, TaskCompletionSource>();
        for (var sequence = 1; sequence <= 2; sequence++) processed[sequence] = Signal();
        scheduler.LineProcessed += line => processed[line.Sequence].TrySetResult();
        using var effect = Line(1, "<sigh>");
        using var silent = Line(2, "<not supplied>");
        scheduler.Enqueue(effect);
        scheduler.Enqueue(silent);
        await Task.WhenAll(processed.Values.Select(signal => signal.Task)).WaitAsync(Timeout, Token);
        Assert.Empty(runtime.Requests);
        Assert.Equal(DubLineState.Buffered, effect.State);
        Assert.Equal(new float[] { .25f, .5f }, await effect.Audio.DrainAsync(Token));
        Assert.False(effect.ApplyBaseCloneCorrection);
        Assert.True(silent.SpeechPlan!.IsSilent);
        Assert.Empty(await silent.Audio.DrainAsync(Token));
    }

    [Fact]
    public async Task SchedulerSeparatesEnabledDisabledAndLegacyCachedAudio()
    {
        using var fixture = new EffectsFixture();
        using var database = new Database(Path.Combine(fixture.Root, "cache.sqlite3"));
        await database.WriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO voice_profile(id,kind,language,model_hash,ref_text,speaker_embedding,rvq_codes,rvq_length,codebooks,profile_hash,created_utc)
                VALUES('profile',1,'english','model','reference',X'00000000',X'00000000',1,1,'hash','2026-11-16')
                """;
            await command.ExecuteNonQueryAsync(Token);
        }, Token);
        var cache = new LineCache(database, Path.Combine(fixture.Root, "cache"), () => 1_000_000);
        var runtime = new RecordingRuntime();
        var enabled = true;
        var reference = new VoiceReference([.1f], [1], 1, 1, "Reference <sigh> line");
        await using var scheduler = new DubScheduler(runtime, (_, _) => ValueTask.FromResult(VoiceResolution.Ready(reference)),
            cache, "model", "english", dialogueSpeech: fixture.Speech, soundEffectsEnabled: () => enabled);
        var sequence = 0;
        async Task<float[]> Run()
        {
            using var line = Line(++sequence, "Before <sigh> after");
            line.VoiceProfileId = "profile";
            line.VoiceProfileHash = "hash";
            var completed = Signal();
            void OnProcessed(DubLine processed) { if (ReferenceEquals(line, processed)) completed.TrySetResult(); }
            scheduler.LineProcessed += OnProcessed;
            try
            {
                scheduler.Enqueue(line);
                await completed.Task.WaitAsync(Timeout, Token);
                Assert.Equal(DubLineState.Buffered, line.State);
                return await line.Audio.DrainAsync(Token);
            }
            finally { scheduler.LineProcessed -= OnProcessed; }
        }
        var first = await Run();
        Assert.Equal(new float[] { .75f, .25f, .5f, .75f }, first);
        Assert.Equal(new[] { "Before", "after" }, runtime.Requests.Select(request => request.Text));
        Assert.All(runtime.Requests, request => Assert.DoesNotContain('<', request.Reference!.Transcript));
        var seed = runtime.Requests[0].Seed;
        using (var legacy = Line(100, "Before <sigh> after"))
            await cache.StoreAsync(legacy, "profile", "hash", "model", "english", seed, new float[] { -.5f }, Token);
        Assert.Equal(first, await Run());
        Assert.Equal(2, runtime.Requests.Count);
        enabled = false;
        Assert.Equal(new float[] { .75f }, await Run());
        Assert.Equal("Before after", runtime.Requests.Last().Text);
        Assert.Equal(3, runtime.Requests.Count);
        enabled = true;
        Assert.Equal(first, await Run());
        Assert.Equal(3, runtime.Requests.Count);
    }

    [Fact]
    public void SanitizingRequestPreservesOriginalAndReferenceAudioData()
    {
        var reference = new VoiceReference([.1f], [1], 1, 1, "One (soupire) two.");
        var original = new SynthesisRequest("One <unknown> two.", "english", reference, null, 123);
        var cleaned = original.WithoutDialogueCues();
        Assert.Equal("One   two.", cleaned.Text);
        Assert.Equal("One   two.", cleaned.Reference!.Transcript);
        Assert.Same(reference.SpeakerEmbedding, cleaned.Reference.SpeakerEmbedding);
        Assert.Same(reference.RvqCodes, cleaned.Reference.RvqCodes);
        Assert.Equal("One <unknown> two.", original.Text);
        Assert.Equal("One (soupire) two.", reference.Transcript);
    }

    [Fact]
    public void ExistingConfigurationsEnableEffectsAndExplicitOptOutPersists()
    {
        Assert.True(JsonSerializer.Deserialize<Configuration>("{}")!.EnableDialogueSoundEffects);
        var saved = JsonSerializer.Serialize(new Configuration { EnableDialogueSoundEffects = false });
        Assert.False(JsonSerializer.Deserialize<Configuration>(saved)!.EnableDialogueSoundEffects);
    }

    [Fact]
    public void AllBundledEffectsAreDecodedPcmAndContentChangesInvalidateCacheIdentity()
    {
        var library = new DialogueSoundEffects(BundledEffects, (id, error) => Assert.Fail($"{id}: {error}"));
        foreach (var name in new[] { "sigh", "gasp", "pant", "blip", "click", "cough", "sniff", "sob", "hic", "groan" })
        {
            Assert.True(library.TryGet(name, out _, out var samples));
            Assert.InRange(samples.Length, 2400, 240_000);
            Assert.Contains(samples.ToArray(), value => Math.Abs(value) > .001f);
            Assert.All(samples.ToArray(), value => Assert.InRange(value, -1f, 1f));
        }
        using var fixture = new EffectsFixture();
        var before = fixture.Speech.Prepare("<sigh>", true).CacheIdentity;
        EffectsFixture.WriteWave(Path.Combine(fixture.Root, "sigh.wav"), [4096]);
        var after = new DialogueSpeech(new DialogueSoundEffects(fixture.Root)).Prepare("<sigh>", true).CacheIdentity;
        Assert.NotEqual(before, after);
    }

    private static string BundledEffects => Path.Combine(AppContext.BaseDirectory, "assets", "sound-effects");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static DubLine Line(long sequence, string text) => new()
    {
        SessionEpoch = 1, Sequence = sequence, SpeakerKey = "npc:cue", SpeakerName = "Cue", Text = text,
        ActualStatus = ActualStatus.Actual,
    };

    private sealed class EffectsFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "cue-test-" + Guid.NewGuid().ToString("N"));
        public DialogueSpeech Speech { get; }
        public EffectsFixture()
        {
            Directory.CreateDirectory(Root);
            WriteWave(Path.Combine(Root, "sigh.wav"), [8192, 16384]);
            WriteWave(Path.Combine(Root, "click-clack.wav"), [4096]);
            Speech = new(new DialogueSoundEffects(Root));
        }
        public static void WriteWave(string path, short[] samples)
        {
            using var writer = new WaveFileWriter(path, new WaveFormat(24000, 16, 1));
            foreach (var sample in samples)
            {
                writer.WriteByte((byte)(sample & 255));
                writer.WriteByte((byte)((sample >> 8) & 255));
            }
        }
        public void Dispose() => TestDirectory.Delete(Root, true);
    }

    private sealed class RecordingRuntime : ITtsRuntime
    {
        public List<SynthesisRequest> Requests { get; } = [];
        public RuntimeCapabilities Capabilities { get; } = new(true, true, true, []);
        public ValueTask<VoiceReference> ExtractReferenceAsync(ReadOnlyMemory<float> samples, string text,
            CancellationToken token) => throw new NotSupportedException();
        public Task SynthesizeAsync(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token)
        {
            Requests.Add(request);
            Assert.True(sink.TryWrite(new float[] { .75f }));
            sink.Complete();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
