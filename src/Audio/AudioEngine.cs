using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Resonance.Scheduling;

namespace Resonance.Audio;

public sealed class AudioEngine : IDisposable
{
    private readonly MixingSampleProvider mixer;
    private readonly IWavePlayer output;
    private readonly object gate = new();
    private StreamingSampleProvider? current;

    public event Action<DubLine>? Started;
    public event Action<DubLine>? Finished;

    public AudioEngine(int deviceNumber = -1)
    {
        mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2)) { ReadFully = true };
        var waveOut = CreateOutput(deviceNumber);
        waveOut.Play();
        output = waveOut;
    }

    private WaveOutEvent CreateOutput(int deviceNumber)
    {
        WaveOutEvent Create(int number)
        {
            var value = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3, DeviceNumber = number };
            try
            {
                value.Init(mixer);
                return value;
            }
            catch
            {
                value.Dispose();
                throw;
            }
        }

        try { return Create(deviceNumber); }
        catch when (deviceNumber >= 0) { return Create(-1); }
    }

    public void Play(DubLine line, float volume)
    {
        Stop();
        var source = new StreamingSampleProvider(line, Math.Clamp(volume, 0f, 2f), OnFinished);
        lock (gate) current = source;
        line.State = DubLineState.Active;
        var resampled = new WdlResamplingSampleProvider(source, 48_000);
        mixer.AddMixerInput(new MonoToStereoSampleProvider(resampled));
        Started?.Invoke(line);
    }

    public void Stop()
    {
        StreamingSampleProvider? source;
        lock (gate) source = current;
        source?.Cancel();
    }

    private void OnFinished(StreamingSampleProvider source)
    {
        lock (gate)
        {
            if (!ReferenceEquals(current, source)) return;
            current = null;
        }
        if (!source.Line.IsTerminal) source.Line.State = DubLineState.Completed;
        Finished?.Invoke(source.Line);
    }

    public void Dispose()
    {
        Stop();
        output.Stop();
        output.Dispose();
    }

    private sealed class StreamingSampleProvider : ISampleProvider
    {
        private readonly float volume;
        private readonly Action<StreamingSampleProvider> completed;
        private AudioChunk? chunk;
        private int sourceIndex;
        private int cancelled;
        private int completionSent;

        public DubLine Line { get; }
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(24_000, 1);

        internal StreamingSampleProvider(DubLine line, float volume, Action<StreamingSampleProvider> completed)
        {
            Line = line;
            this.volume = volume;
            this.completed = completed;
            line.Audio.MarkConsumerStarted();
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (Volatile.Read(ref cancelled) != 0) return Finish();
            var written = 0;
            var consumed = 0;
            while (written < count)
            {
                if (!EnsureChunk())
                {
                    Line.Audio.ReportConsumed(consumed);
                    if (Line.Audio.ProducerCompleted) return written == 0 ? Finish() : written;
                    Array.Clear(buffer, offset + written, count - written); // transient underrun; keep input alive
                    return count;
                }

                var source = chunk!.Samples.Span;
                var sample = source[sourceIndex] * volume;
                buffer[offset + written++] = sample;
                consumed++;
                sourceIndex++;
                if (sourceIndex >= chunk.Count)
                {
                    chunk.Dispose();
                    chunk = null;
                    sourceIndex = 0;
                }
            }
            Line.Audio.ReportConsumed(consumed);
            return written;
        }

        private bool EnsureChunk()
        {
            if (chunk is not null) return true;
            return Line.Audio.Reader.TryRead(out chunk);
        }

        internal void Cancel()
        {
            Interlocked.Exchange(ref cancelled, 1);
            Line.Cancel();
        }

        private int Finish()
        {
            chunk?.Dispose();
            chunk = null;
            if (Interlocked.Exchange(ref completionSent, 1) == 0) completed(this);
            return 0;
        }
    }
}
