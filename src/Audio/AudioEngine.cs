using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Resonance.Scheduling;

namespace Resonance.Audio;

public sealed class AudioEngine : IDisposable
{
    private readonly MixingSampleProvider mixer;
    private readonly IWavePlayer output;
    private readonly object gate = new();
    private readonly object disposeGate = new();
    private StreamingSampleProvider? current;
    private ISampleProvider? currentInput;
    private TaskCompletionSource? disposeCompletion;
    private int disposed;

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
        if (Volatile.Read(ref disposed) != 0)
        {
            line.Dispose();
            return;
        }
        var source = new StreamingSampleProvider(line, Math.Clamp(volume, 0f, 2f), OnFinished);
        var accepted = false;
        lock (gate)
        {
            accepted = Volatile.Read(ref disposed) == 0;
            if (accepted) current = source;
        }
        if (!accepted)
        {
            source.Dispose();
            return;
        }
        if (!line.TryTransition(
                DubLineState.Active,
                DubLineState.Predicted,
                DubLineState.VoiceResolving,
                DubLineState.Queued,
                DubLineState.Generating,
                DubLineState.Buffered))
        {
            lock (gate)
            {
                if (ReferenceEquals(current, source)) current = null;
            }
            source.CancelAndDispose();
            return;
        }
        var resampled = new WdlResamplingSampleProvider(source, 48_000);
        var mixerInput = new MonoToStereoSampleProvider(resampled);
        try { mixer.AddMixerInput(mixerInput); }
        catch
        {
            RemoveMixerInput(mixerInput);
            lock (gate)
            {
                if (ReferenceEquals(current, source)) current = null;
            }
            source.CancelAndDispose();
            throw;
        }
        var removeInput = false;
        lock (gate)
        {
            if (ReferenceEquals(current, source) && Volatile.Read(ref disposed) == 0)
                currentInput = mixerInput;
            else
                removeInput = true;
        }
        if (removeInput)
        {
            try { RemoveMixerInput(mixerInput); }
            finally { source.CancelAndDispose(); }
            return;
        }
        var stillCurrent = false;
        lock (gate) stillCurrent = ReferenceEquals(current, source) && Volatile.Read(ref disposed) == 0;
        if (!stillCurrent)
        {
            source.CancelAndDispose();
            return;
        }
        try { Started?.Invoke(line); }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        StreamingSampleProvider? source;
        ISampleProvider? mixerInput;
        lock (gate)
        {
            source = current;
            current = null;
            mixerInput = currentInput;
            currentInput = null;
        }
        try { RemoveMixerInput(mixerInput); }
        finally { source?.CancelAndDispose(); }
    }

    private void OnFinished(StreamingSampleProvider source)
    {
        var ownsSource = false;
        ISampleProvider? mixerInput = null;
        lock (gate)
        {
            ownsSource = ReferenceEquals(current, source);
            if (ownsSource)
            {
                current = null;
                mixerInput = currentInput;
                currentInput = null;
            }
        }
        if (!ownsSource)
        {
            source.Dispose();
            return;
        }
        var completed = false;
        try
        {
            RemoveMixerInput(mixerInput);
            completed = source.Line.TryTransition(DubLineState.Completed, DubLineState.Active);
        }
        finally { source.Dispose(); }
        if (completed) Finished?.Invoke(source.Line);
    }

    public void Dispose()
    {
        Task task;
        lock (disposeGate)
        {
            if (disposeCompletion is null)
            {
                Interlocked.Exchange(ref disposed, 1);
                var completion = disposeCompletion = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _ = completion.Task.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                try { _ = Task.Run(() => DisposeCore(completion)); }
                catch (Exception error) { completion.TrySetException(error); }
            }
            task = disposeCompletion.Task;
        }
        if (!StreamingSampleProvider.IsInReadCallback)
            task.GetAwaiter().GetResult();
    }

    private void DisposeCore(TaskCompletionSource completion)
    {
        try
        {
            try { Stop(); }
            finally
            {
                try { output.Stop(); }
                finally { output.Dispose(); }
            }
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private void RemoveMixerInput(ISampleProvider? input)
    {
        if (input is null) return;
        try { mixer.RemoveMixerInput(input); }
        catch (InvalidOperationException) { }
    }

    private sealed class StreamingSampleProvider : ISampleProvider
    {
        private readonly float volume;
        private readonly Action<StreamingSampleProvider> completed;
        private readonly object readSerialGate = new();
        private readonly object readGate = new();
        private AudioChunk? chunk;
        private int sourceIndex;
        private int cancelled;
        private int completionSent;
        private int activeReaders;
        private bool disposeRequested;
        private bool resourcesDisposed;
        [ThreadStatic] private static int readDepth;

        internal static bool IsInReadCallback => readDepth != 0;

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
            lock (readSerialGate) return ReadCore(buffer, offset, count);
        }

        private int ReadCore(float[] buffer, int offset, int count)
        {
            if (!EnterReader()) return Finish();
            readDepth++;
            try
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
            finally
            {
                readDepth--;
                ExitReader();
            }
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

        internal void CancelAndDispose()
        {
            Cancel();
            Dispose();
        }

        internal void Dispose()
        {
            Interlocked.Exchange(ref cancelled, 1);
            var finalize = false;
            lock (readGate)
            {
                if (resourcesDisposed) return;
                disposeRequested = true;
                finalize = activeReaders == 0;
            }
            Line.Cancel();
            if (finalize) FinalizeDispose();
        }

        private int Finish()
        {
            AudioChunk? pending;
            lock (readGate)
            {
                pending = chunk;
                chunk = null;
                sourceIndex = 0;
            }
            pending?.Dispose();
            if (Interlocked.Exchange(ref completionSent, 1) == 0) completed(this);
            return 0;
        }

        private bool EnterReader()
        {
            lock (readGate)
            {
                if (resourcesDisposed || disposeRequested) return false;
                activeReaders++;
                return true;
            }
        }

        private void ExitReader()
        {
            var finalize = false;
            lock (readGate)
            {
                if (activeReaders > 0) activeReaders--;
                finalize = activeReaders == 0 && disposeRequested && !resourcesDisposed;
            }
            if (finalize) FinalizeDispose();
        }

        private void FinalizeDispose()
        {
            AudioChunk? pending;
            lock (readGate)
            {
                if (resourcesDisposed || activeReaders != 0) return;
                resourcesDisposed = true;
                pending = chunk;
                chunk = null;
            }
            pending?.Dispose();
            Line.Dispose();
        }
    }
}
