using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.SQEX.CDev.Engine.Sd.Driver;
using FFXIVClientStructs.SQEX.Sd.Driver.Core;

namespace Resonance.Audio;

public sealed unsafe class FfxivPcmSink : IGameMixerPcmSink
{
    private const string RenderSignature = "48 89 5C 24 20 56 57 41 55 41 56 41 57 48 83 EC 70 0F B7 81 22 01 00 00 48 8B D9";
    private delegate int RenderDelegate(CoreSubmixVoice* voice);
    private readonly Hook<RenderDelegate> render;
    private readonly delegate* unmanaged<CoreSubmixVoice*, int> nativeRender;
    private GameMixerPcmStream? active;
    private int disposed;
    private string? fault;
    private int hookDisabled;
    private int callbacks;

    public FfxivPcmSink(ISigScanner scanner, IGameInteropProvider interop)
    {
        if (*CoreSystem.SampleRate() != GameMixerPcmStream.SampleRate || *CoreSystem.ChannelCount() != 8
            || Bus.GetById(3) == null)
            throw new InvalidOperationException("FFXIV PCM mixer is not initialized");
        var address = scanner.ScanText(RenderSignature);
        nativeRender = (delegate* unmanaged<CoreSubmixVoice*, int>)(void*)address;
        render = interop.HookFromAddress<RenderDelegate>(address, Render);
        try { render.Enable(); }
        catch { render.Dispose(); throw; }
    }

    public bool IsAvailable => Volatile.Read(ref disposed) == 0 && Volatile.Read(ref fault) is null
        && *CoreSystem.SampleRate() == GameMixerPcmStream.SampleRate
        && *CoreSystem.ChannelCount() == 8;
    public string Diagnostic => Volatile.Read(ref fault) ?? (IsAvailable ? "FFXIV incremental PCM mixer ready" : "FFXIV PCM mixer unavailable");
    public void Play(GameMixerPcmStream stream) => Interlocked.Exchange(ref active, stream);
    public void Stop(GameMixerPcmStream stream)
    {
        stream.Stop();
        Interlocked.CompareExchange(ref active, null, stream);
    }

    private int Render(CoreSubmixVoice* voice)
    {
        Interlocked.Increment(ref callbacks);
        try
        {
            if (Volatile.Read(ref hookDisabled) != 0) return nativeRender(voice);
            try { Mix(voice); }
            catch (Exception error) { Volatile.Write(ref fault, $"FFXIV PCM mixer failed: {error.Message}"); }
            return render.Original(voice);
        }
        finally { Interlocked.Decrement(ref callbacks); }
    }

    private void Mix(CoreSubmixVoice* voice)
    {
        var stream = Volatile.Read(ref active);
        var voices = CoreSubmixVoice.Instances();
        if (stream is not null && voices != null && voice == voices[0] && voice->SendCount > 0)
        {
            var frames = *CoreSystem.FrameCount();
            if (frames is > 0 and <= 4096)
            {
                var bus = Bus.GetById(3);
                if (bus != null)
                {
                    Span<float> samples = stackalloc float[frames * 8];
                    if (stream.Render(samples, bus->GetTotalVolume()) > 0)
                        fixed (float* pointer = samples) voice->MixIn(pointer, (uint)frames, 1f, 0);
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.Exchange(ref active, null)?.Stop();
        render.Disable();
        Volatile.Write(ref hookDisabled, 1);
        // Late detours use the restored native entry; existing callbacks retain their trampoline.
        SpinWait.SpinUntil(() => Volatile.Read(ref callbacks) == 0);
        render.Dispose();
    }
}
