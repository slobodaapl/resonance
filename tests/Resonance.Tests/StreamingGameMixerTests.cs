using Resonance.Audio;
using Resonance.Plugin;
using Resonance.Scheduling;

namespace Resonance.Tests;

public sealed class StreamingGameMixerTests
{
    [Fact]
    public void OutputMeterMeasuresRenderedChannelsRatherThanQueuedOrFutureSpeech()
    {
        var stream = new GameMixerPcmStream { Volume = .5f };
        stream.Write([-.4f, .2f, 1f]);
        Assert.Equal(0, stream.OutputPeak);
        var output = new float[16];
        Assert.Equal(2, stream.Render(output, .5f));
        Assert.InRange(stream.OutputPeak, .0707106f, .0707108f);
        Assert.Equal(output.Max(Math.Abs), stream.OutputPeak);
        Assert.Equal(1, stream.Render(output, .5f));
        Assert.InRange(stream.OutputPeak, .1767766f, .1767768f);
        Assert.Equal(0, stream.Render(output, 1));
        Assert.Equal(0, stream.OutputPeak);
        stream.Write([1]);
        stream.Render(output, 1);
        Assert.True(stream.OutputPeak > 0);
        stream.Stop();
        Assert.Equal(0, stream.OutputPeak);
    }

    [Fact]
    public async Task OutputMeterDropsStaleSignalWhenTheNativeMixerStopsCallingRender()
    {
        var stream = new GameMixerPcmStream();
        stream.Write([1]);
        stream.Render(new float[8], 1);
        Assert.True(stream.OutputPeak > 0);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(0, stream.OutputPeak);
    }

    [Fact]
    public void UnderrunWaitsAndResumesWithoutLosingSamplesOrClaimingCompletion()
    {
        var stream = new GameMixerPcmStream();
        stream.Write([1, 0.5f]);
        var output = new float[32];
        Assert.Equal(2, stream.Render(output, 0.5f));
        Assert.InRange(output[0], 0.35355f, 0.35356f);
        Assert.Equal(output[0], output[1]);
        Assert.Equal(0, output[2]);
        Assert.Equal(0, output[16]);
        Assert.False(stream.IsDrained);
        Assert.Equal(0, stream.Render(output, 1));
        Assert.All(output, value => Assert.Equal(0, value));
        stream.Write([-1]);
        stream.Complete();
        Assert.False(stream.IsDrained);
        Assert.Equal(1, stream.Render(output, 1));
        Assert.InRange(output[0], -0.70711f, -0.70710f);
        Assert.Equal(3, stream.RenderedSamples);
        Assert.True(stream.IsDrained);
    }

    [Fact]
    public void StoppingPreventsFurtherRenderingIncludingLateProducerWrites()
    {
        var stream = new GameMixerPcmStream();
        stream.Write([1, 2]);
        stream.Stop();
        stream.Write([3]);
        Assert.Equal(0, stream.Render(new float[32], 1));
        Assert.Equal(0, stream.RenderedSamples);
    }

    [Fact]
    public async Task PlaybackStartsBeforeProducerCompletionAndFinishesOnlyAfterRenderDrain()
    {
        var sink = new Sink();
        using var backend = new StreamingGameMixerAudioBackend(sink);
        using var line = Line();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        line.Audio.TryWrite(Enumerable.Repeat(0.2f, 4800).ToArray());
        var first = backend.PrepareAsync(line, 1, TestContext.Current.CancellationToken);
        var second = backend.PrepareAsync(line, 1, TestContext.Current.CancellationToken);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(line.Audio.ProducerCompleted);
        backend.Play(line, 0.5f, _ => started.TrySetResult(), _ => finished.TrySetResult(),
            (_, error) => finished.TrySetException(error));
        var stream = await sink.Played.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var output = new float[256 * 8];
        Assert.True(stream.Render(output, 1) > 0);
        Assert.InRange(output.Max(), 0.06f, 0.08f);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(finished.Task.IsCompleted);
        Assert.Equal(DubLineState.Active, line.State);
        line.Audio.Complete();
        await Drain(stream);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(DubLineState.Completed, line.State);
        Assert.InRange(stream.RenderedSamples, 9598, 9602);
    }

    [Fact]
    public async Task ProducerFailureFailsLineWithoutSuccessfulCompletion()
    {
        var sink = new Sink();
        using var backend = new StreamingGameMixerAudioBackend(sink);
        using var line = Line();
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = false;
        line.Audio.Complete(new InvalidDataException("producer failure"));
        backend.Play(line, 1, _ => { }, _ => completed = true, (_, error) => failed.TrySetResult(error));
        var error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.IsType<InvalidDataException>(error);
        Assert.False(completed);
        Assert.Equal(DubLineState.Failed, line.State);
    }

    [Fact]
    public async Task RepeatedPlayCannotInterruptTheActiveLine()
    {
        var sink = new Sink();
        using var engine = new AudioEngine(new StreamingGameMixerAudioBackend(sink));
        using var line = Line();
        line.ReplaceAudio(StreamingAudioBuffer.FromSamples(Enumerable.Repeat(0.1f, 2400).ToArray()));
        engine.Play(line, 1);
        var stream = await sink.Played.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        engine.Play(line, 1);
        Assert.True(stream.Render(new float[256 * 8], 1) > 0);
        Assert.Equal(DubLineState.Active, line.State);
    }

    [Fact]
    public async Task EmptySpeechCannotCompleteOrAdvanceALine()
    {
        using var backend = new StreamingGameMixerAudioBackend(new Sink());
        using var line = Line();
        line.Audio.Complete();
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        backend.Play(line, 1, _ => { }, _ => finished = true, (_, error) => failed.TrySetResult(error));
        Assert.IsType<InvalidDataException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.False(finished);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NonFiniteSamplesNeverReachTheNativeMixer(bool filter, bool normalization)
    {
        var sink = new Sink();
        using var backend = new StreamingGameMixerAudioBackend(sink, () => new(filter, normalization));
        using var line = Line();
        line.ApplyBaseCloneCorrection = true;
        line.Audio.TryWrite([float.NaN]);
        line.Audio.Complete();
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Play(line, 1, _ => { }, _ => failed.TrySetException(new Exception("Invalid audio completed")),
            (_, error) => failed.TrySetResult(error));
        Assert.IsType<InvalidDataException>(await failed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.False(sink.Played.Task.IsCompleted);
    }

    [Fact]
    public async Task PredictionLongerThanProducerQueueCapacityPreparesWithoutPlayback()
    {
        var sink = new Sink();
        using var backend = new StreamingGameMixerAudioBackend(sink);
        using var line = Line();
        var preparation = backend.PrepareAsync(line, 1, TestContext.Current.CancellationToken);
        var samples = Enumerable.Repeat(0.1f, 24_000).ToArray();
        for (var second = 0; second < 35; second++)
            Assert.True(await line.Audio.WriteAsync(samples, TestContext.Current.CancellationToken));
        line.Audio.Complete();
        await preparation.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(sink.Played.Task.IsCompleted);
        Assert.True(line.PlaybackAssetReady);
    }

    [Fact]
    public async Task ReplacementCannotReceiveOldCompletionOrBeStoppedByOldCleanup()
    {
        var sink = new Sink();
        using var backend = new StreamingGameMixerAudioBackend(sink);
        using var first = Line();
        using var second = Line();
        first.ReplaceAudio(StreamingAudioBuffer.FromSamples(Enumerable.Repeat(0.1f, 2400).ToArray()));
        second.ReplaceAudio(StreamingAudioBuffer.FromSamples(Enumerable.Repeat(0.2f, 2400).ToArray()));
        await backend.PrepareAsync(first, 1, TestContext.Current.CancellationToken);
        await backend.PrepareAsync(second, 1, TestContext.Current.CancellationToken);
        var oldFinished = false;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Play(first, 1, _ => { }, _ => oldFinished = true, (_, error) => finished.TrySetException(error));
        var oldStream = sink.Active!;
        backend.Play(second, 1, _ => { }, _ => finished.TrySetResult(), (_, error) => finished.TrySetException(error));
        var replacement = sink.Active!;
        Assert.NotSame(oldStream, replacement);
        Assert.Equal(0, oldStream.Render(new float[256 * 8], 1));
        await Drain(replacement);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(oldFinished);
        Assert.Equal(DubLineState.Completed, second.State);
    }

    [Fact]
    public async Task ChunkBoundariesDoNotChangeResamplingOrCorrection()
    {
        var samples = Enumerable.Range(0, 12_000).Select(index => 0.1f * MathF.Sin(index * 0.07f)).ToArray();
        var whole = await Render(samples, 12_000);
        var divided = await Render(samples, 173);
        Assert.Equal(whole.Length, divided.Length);
        Assert.Equal(whole, divided);
        Assert.True(whole.Length >= samples.Length * 2);
        Assert.InRange(whole.Max(), 0.05f, 0.3f);
        var legacy = GameMixerPcmEncoder.PrepareMono44100(samples, true).Samples;
        static double Rms(float[] values) => Math.Sqrt(values.Average(value => (double)value * value));
        Assert.InRange(Rms(whole) / (Rms(legacy) * Math.Sqrt(0.5)), 0.97, 1.03);
    }

    [Fact]
    public async Task LoudPassageAfterQuietCalibrationRemainsASineRatherThanClipping()
    {
        var source = Enumerable.Range(0, 29_760).Select(index =>
            (index < 5760 ? 0.01f : 0.9f) * MathF.Sin(2 * MathF.PI * 900 * index / 24_000)).ToArray();
        var output = await Render(source, 173);
        var steady = output.AsSpan(40_000, 4800).ToArray();
        var sine = 0d;
        var cosine = 0d;
        for (var index = 0; index < steady.Length; index++)
        {
            var phase = 2 * Math.PI * 900 * index / 48_000;
            sine += steady[index] * Math.Sin(phase) * 2 / steady.Length;
            cosine += steady[index] * Math.Cos(phase) * 2 / steady.Length;
        }
        var residual = 0d;
        var power = 0d;
        for (var index = 0; index < steady.Length; index++)
        {
            var phase = 2 * Math.PI * 900 * index / 48_000;
            var difference = steady[index] - sine * Math.Sin(phase) - cosine * Math.Cos(phase);
            residual += difference * difference;
            power += (double)steady[index] * steady[index];
        }
        Assert.InRange(Math.Sqrt(residual / power), 0, 0.01);
        Assert.InRange(steady.Max(), 0.5f, 0.7f);
    }

    [Theory]
    [InlineData(0.72, 0)]
    [InlineData(2.64, 0.00001f)]
    public async Task LeadingSilenceDoesNotDetermineTheVoicedPassagesGain(double silenceSeconds, float noise)
    {
        var speech = Enumerable.Range(0, 24_000)
            .Select(index => 0.03f * MathF.Sin(2 * MathF.PI * 900 * index / 24_000)).ToArray();
        var silentSamples = (int)(silenceSeconds * 24_000);
        var delayed = Enumerable.Repeat(noise, silentSamples).Concat(speech).ToArray();
        var reference = await Render(speech, 173);
        var withSilence = await Render(delayed, 173);
        static double Rms(ReadOnlySpan<float> values)
        {
            double sum = 0;
            foreach (var value in values) sum += (double)value * value;
            return Math.Sqrt(sum / values.Length);
        }
        // After 25 gain-release blocks, the remaining ramp error is below 0.4%.
        var referenceRms = Rms(reference.AsSpan(24_000, 12_000));
        var delayedRms = Rms(withSilence.AsSpan(silentSamples * 2 + 24_000, 12_000));
        Assert.InRange(delayedRms / referenceRms, 0.99, 1.01);
        Assert.Equal(reference.Length + silentSamples * 2, withSilence.Length);
    }

    [Fact]
    public async Task SilentCorrectedAudioStillDrainsAndPreservesItsDuration()
    {
        var source = new float[24_000];
        var output = await Render(source, 173);
        Assert.InRange(output.Length, 48_000, 49_024);
        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StreamingClonedVoiceProcessingStagesCanBeBypassedIndependently(bool filter, bool normalization)
    {
        var configuration = new Configuration
        {
            EnableVoiceCorrection = filter,
            EnableLineNormalization = normalization,
        };
        var source = Enumerable.Range(0, 24_000)
            .Select(index => .03f * MathF.Sin(2 * MathF.PI * 160 * index / 24_000)).ToArray();

        var output = await Render(source, 173, configuration.GetAudioProcessingOptions);
        var steady = output.AsSpan(12_000, 24_000).ToArray();
        var rms = Math.Sqrt(steady.Average(value => (double)value * value));
        var expected = .015 * Math.Pow(10, ((filter ? -.825 : 0) + (normalization ? 6 : 0)) / 20);

        // The filter's measured 160 Hz response spans 0.07 dB; 1% also allows resampling error.
        Assert.InRange(rms, expected * .99, expected * 1.01);
    }

    [Fact]
    public async Task ProcessingChangesAffectNewPreparationsWhilePreparedSpeechKeepsItsSnapshot()
    {
        var configuration = new Configuration();
        var sink = new Sink();
        using var backend = new StreamingGameMixerAudioBackend(sink, configuration.GetAudioProcessingOptions);
        var source = Enumerable.Range(0, 24_000)
            .Select(index => .03f * MathF.Sin(2 * MathF.PI * 160 * index / 24_000)).ToArray();
        using var first = Line();
        first.ApplyBaseCloneCorrection = true;
        first.ReplaceAudio(StreamingAudioBuffer.FromSamples(source));
        configuration.EnableVoiceCorrection = false;
        configuration.EnableLineNormalization = false;
        await backend.PrepareAsync(first, 1, TestContext.Current.CancellationToken);
        configuration.EnableVoiceCorrection = true;
        configuration.EnableLineNormalization = true;
        var original = await PlayPrepared(first);
        using var second = Line();
        second.ApplyBaseCloneCorrection = true;
        second.ReplaceAudio(StreamingAudioBuffer.FromSamples(source));
        await backend.PrepareAsync(second, 1, TestContext.Current.CancellationToken);
        var updated = await PlayPrepared(second);

        static double Rms(float[] samples) => Math.Sqrt(samples.AsSpan(12_000, 24_000).ToArray()
            .Average(value => (double)value * value));
        Assert.InRange(Rms(original), .01485, .01515);
        Assert.InRange(Rms(updated) / Rms(original), 1.79, 1.84);

        async Task<float[]> PlayPrepared(DubLine line)
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.Play(line, 1, _ => { }, _ => finished.TrySetResult(), (_, error) => finished.TrySetException(error));
            var result = new List<float>();
            await Drain(Assert.IsType<GameMixerPcmStream>(sink.Active), result);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            return result.ToArray();
        }
    }

    private static async Task<float[]> Render(
        float[] samples, int chunkSize, Func<AudioProcessingOptions>? processingOptions = null)
    {
        var sink = new Sink();
        using var backend = new StreamingGameMixerAudioBackend(sink, processingOptions);
        using var line = Line();
        line.ApplyBaseCloneCorrection = true;
        line.ReplaceAudio(StreamingAudioBuffer.FromSamples(samples, chunkSize));
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Play(line, 1, _ => { }, _ => finished.TrySetResult(), (_, error) => finished.TrySetException(error));
        var stream = await sink.Played.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var result = new List<float>();
        await Drain(stream, result);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        return result.ToArray();
    }

    private static async Task Drain(GameMixerPcmStream stream, List<float>? samples = null)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var buffer = new float[256 * 8];
        while (!stream.IsDrained)
        {
            Assert.True(DateTime.UtcNow < timeout, "PCM stream did not drain");
            var count = stream.Render(buffer, 1);
            for (var index = 0; index < count; index++) samples?.Add(buffer[index * 8]);
            if (count == 0) await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }

    private static DubLine Line() => new()
    {
        SessionEpoch = 1, Sequence = 1, SpeakerKey = "npc:1", SpeakerName = "Speaker",
        Text = "A long spoken line", ActualStatus = ActualStatus.Actual,
    };

    private sealed class Sink : IGameMixerPcmSink
    {
        public readonly TaskCompletionSource<GameMixerPcmStream> Played = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GameMixerPcmStream? Active;
        public bool IsAvailable => true;
        public string Diagnostic => "test";
        public void Play(GameMixerPcmStream stream) { Active = stream; Played.TrySetResult(stream); }
        public void Stop(GameMixerPcmStream stream)
        {
            stream.Stop();
            Interlocked.CompareExchange(ref Active, null, stream);
        }
        public void Dispose() { }
    }
}
