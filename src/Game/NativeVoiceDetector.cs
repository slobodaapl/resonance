using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace Resonance.Game;

public sealed record NativeVoiceObservation(string ScdPath, DateTimeOffset StartedAt);

public sealed partial class NativeVoiceDetector : IDisposable
{
    private const string LoadSoundFileSignature = "E8 ?? ?? ?? ?? 48 85 C0 75 05 40 B7 F6";
    private const string PlaySpecificSoundSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 33 F6 8B DA 48 8B F9 0F BA E2 0F";
    private const string PlayClipSoundSignature =
        "E8 ?? ?? ?? ?? 48 85 C0 74 ?? 48 89 43 ?? 48 8B 4B ?? 48 85 C9 74 ?? ?? ?? ?? FF 50 ?? 84 C0 75";

    private delegate nint LoadSoundFileDelegate(nint resourceHandle, uint argument);
    private delegate nint PlaySpecificSoundDelegate(nint sound, int argument);
    private delegate nint PlayClipSoundDelegate(nint manager, nint path, float volume, uint fadeInDuration,
        float x, float y, float z, float speed, int priority, uint soundNumber, byte autoRelease, byte argument12);

    private readonly object gate = new();
    private readonly HashSet<nint> knownPointers = [];
    private readonly Dictionary<nint, string> paths = [];
    private readonly IPluginLog log;
    private Hook<LoadSoundFileDelegate>? loadHook;
    private Hook<PlaySpecificSoundDelegate>? playHook;
    private Hook<PlayClipSoundDelegate>? clipHook;

    private static readonly int ResourceDataOffset = Marshal.SizeOf<ResourceHandle>();
    private static readonly int SoundDataOffset = Marshal.SizeOf<nint>();

    public bool IsAvailable { get; private set; }
    public string? UnavailableReason { get; private set; }
    public event Action<NativeVoiceObservation>? TalkVoiceStarted;
    public event Action<OfficialVoiceClipObservation>? OfficialVoiceClipObserved;

    public NativeVoiceDetector(ISigScanner scanner, IGameInteropProvider interop, IPluginLog log)
    {
        this.log = log;
        try
        {
            if (!scanner.TryScanText(LoadSoundFileSignature, out var loadAddress))
                throw new InvalidOperationException("sound resource-load signature not found");
            if (!scanner.TryScanText(PlaySpecificSoundSignature, out var playAddress))
                throw new InvalidOperationException("sound playback signature not found");
            loadHook = interop.HookFromAddress<LoadSoundFileDelegate>(loadAddress, OnLoadSoundFile);
            playHook = interop.HookFromAddress<PlaySpecificSoundDelegate>(playAddress, OnPlaySpecificSound);
            loadHook.Enable();
            playHook.Enable();
            if (scanner.TryScanText(PlayClipSoundSignature, out var clipAddress))
            {
                clipHook = interop.HookFromAddress<PlayClipSoundDelegate>(clipAddress, OnPlayClipSound);
                clipHook.Enable();
            }
            else log.Warning("Higher-level cutscene VO hook unavailable; official voice learning is disabled");
            IsAvailable = true;
        }
        catch (Exception error)
        {
            UnavailableReason = error.Message;
            loadHook?.Dispose();
            playHook?.Dispose();
            clipHook?.Dispose();
            loadHook = null;
            playHook = null;
            log.Error(error, "Native VO guard unavailable; synthetic playback is suppressed");
        }
    }

    private nint OnPlayClipSound(nint manager, nint pathPointer, float volume, uint fadeInDuration,
        float x, float y, float z, float speed, int priority, uint soundNumber, byte autoRelease, byte argument12)
    {
        var result = clipHook!.Original(manager, pathPointer, volume, fadeInDuration, x, y, z, speed, priority,
            soundNumber, autoRelease, argument12);
        try
        {
            var path = Marshal.PtrToStringUTF8(pathPointer);
            if (path is not null && CutsceneVoicePath().IsMatch(path))
                OfficialVoiceClipObserved?.Invoke(new(path, soundNumber, DateTimeOffset.UtcNow));
        }
        catch (Exception error) { log.Warning(error, "Official voice clip observation failed"); }
        return result;
    }

    private unsafe nint OnLoadSoundFile(nint resourceHandle, uint argument)
    {
        var result = loadHook!.Original(resourceHandle, argument);
        try
        {
            var path = ((ResourceHandle*)resourceHandle)->FileName.ToString();
            if (!path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)) return result;
            var data = Marshal.ReadIntPtr(resourceHandle + ResourceDataOffset);
            if (data == 0) return result;
            var voice = CutsceneVoicePath().IsMatch(path) && !IgnoredPath().IsMatch(path);
            lock (gate)
            {
                if (voice) { knownPointers.Add(data); paths[data] = path; }
                else { knownPointers.Remove(data); paths.Remove(data); }
            }
        }
        catch (Exception error) { log.Warning(error, "Native VO resource observation failed"); }
        return result;
    }

    private nint OnPlaySpecificSound(nint sound, int argument)
    {
        var result = playHook!.Original(sound, argument);
        try
        {
            var data = Marshal.ReadIntPtr(sound + SoundDataOffset);
            string? path;
            lock (gate) path = knownPointers.Contains(data) && paths.TryGetValue(data, out var value) ? value : null;
            if (path is not null) TalkVoiceStarted?.Invoke(new(path, DateTimeOffset.UtcNow));
        }
        catch (Exception error) { log.Warning(error, "Native VO playback observation failed"); }
        return result;
    }

    [GeneratedRegex(@"^cut/.*/(vo_|voice)", RegexOptions.IgnoreCase)]
    private static partial Regex CutsceneVoicePath();

    [GeneratedRegex(@"^(bgcommon|music|sound/(battle|foot|instruments|strm|vfx|voice/Vo_Emote|zingle))/", RegexOptions.IgnoreCase)]
    private static partial Regex IgnoredPath();

    public void Dispose()
    {
        IsAvailable = false;
        loadHook?.Dispose();
        playHook?.Dispose();
        clipHook?.Dispose();
        lock (gate) { knownPointers.Clear(); paths.Clear(); }
    }
}

public sealed record OfficialVoiceClipObservation(string ScdPath, uint SoundNumber, DateTimeOffset StartedAt);
