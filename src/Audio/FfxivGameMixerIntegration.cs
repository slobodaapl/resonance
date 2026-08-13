using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using FFXIVClientStructs.FFXIV.Client.System.File;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using InteropGenerator.Runtime;
using System.Runtime.InteropServices;
using GameFileMode = FFXIVClientStructs.FFXIV.Client.System.File.FileMode;

namespace Resonance.Audio;

/// <summary>Redirects only Resonance's content-addressed SCD paths to owned local files.</summary>
public sealed unsafe class ResonanceScdResourceOverride : IGameResourceOverride
{
    private const char PathTokenPrefix = (char)((byte)'R' | (('?' & 0x00FF) << 8));
    private const string ReadSqPackSignature = "40 56 41 56 48 83 EC ?? 0F BE 02";
    private const string ReadFileSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 55 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 63 42";
    private const string SoundOnLoadSignature =
        "40 56 57 41 54 48 81 EC ?? ?? ?? ?? 80 3A ?? 45 0F B6 E0 48 8B F2 48 8B F9 75 ?? 83 BA ?? ?? ?? ?? ?? 72 ?? 48 8B 01 FF 90 ?? ?? ?? ?? 3C";
    private const string LoadScdFileLocalSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 8B 79 ?? 48 8B DA 8B D7";

    [StructLayout(LayoutKind.Explicit)]
    private struct DescriptorOverlay
    {
        [FieldOffset(0x00)] public GameFileMode FileMode;
        [FieldOffset(0x30)] public void* FileInterface;
        [FieldOffset(0x50)] public FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.ResourceHandle* ResourceHandle;
        [FieldOffset(0x70)] public char FilePath;
    }

    private delegate nint GetResourceSyncDelegate(nint resourceManager, nint category, nint resourceType,
        int* resourceHash, byte* path, nint parameters, byte* file, uint line);
    private delegate nint GetResourceAsyncDelegate(nint resourceManager, nint category, nint resourceType,
        int* resourceHash, byte* path, nint parameters, byte hasHandleLock, byte* file, uint line);
    private delegate byte ReadSqPackDelegate(nint resourceManager, DescriptorOverlay* descriptor, int priority, bool isSync);
    private delegate byte ReadFileDelegate(nint resourceManager, DescriptorOverlay* descriptor, int priority, byte isSync);
    private delegate byte SoundOnLoadDelegate(
        FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.ResourceHandle* handle,
        DescriptorOverlay* descriptor, byte unknown);
    private delegate nint CreateFileWDelegate(char* fileName, uint access, uint shareMode, nint security,
        uint creation, uint flags, nint template);

    private sealed record Mapping(string LocalPath, nint Utf8Path, int Utf8Length, int References);
    private readonly object mappingGate = new();
    private readonly Dictionary<string, Mapping> mappings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<nint> retiredPaths = [];
    private readonly Hook<GetResourceSyncDelegate>? getResourceSyncHook;
    private readonly Hook<GetResourceAsyncDelegate>? getResourceAsyncHook;
    private readonly Hook<ReadSqPackDelegate>? readSqPackHook;
    private readonly Hook<SoundOnLoadDelegate>? soundOnLoadHook;
    private readonly Hook<CreateFileWDelegate>? createFileHook;
    private readonly ReadFileDelegate? readFile;
    private readonly SoundOnLoadDelegate? loadScdFileLocal;
    private readonly IPluginLog log;
    private readonly ThreadLocal<nint> createFilePath = new(() => Marshal.AllocHGlobal(2 * 264), true);
    private string? unavailableReason;
    private int disposed;

    public ResonanceScdResourceOverride(
        ISigScanner scanner,
        IGameInteropProvider interop,
        IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(interop);
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        try
        {
            getResourceSyncHook = interop.HookFromAddress<GetResourceSyncDelegate>(
                (nint)ResourceManager.MemberFunctionPointers.GetResourceSync, GetResourceSyncDetour);
            getResourceAsyncHook = interop.HookFromAddress<GetResourceAsyncDelegate>(
                (nint)ResourceManager.MemberFunctionPointers.GetResourceAsync, GetResourceAsyncDetour);
            var readFileAddress = scanner.ScanText(ReadFileSignature);
            readFile = Marshal.GetDelegateForFunctionPointer<ReadFileDelegate>(readFileAddress);
            loadScdFileLocal = Marshal.GetDelegateForFunctionPointer<SoundOnLoadDelegate>(
                scanner.ScanText(LoadScdFileLocalSignature));
            readSqPackHook = interop.HookFromSignature<ReadSqPackDelegate>(
                ReadSqPackSignature, ReadSqPackDetour);
            soundOnLoadHook = interop.HookFromSignature<SoundOnLoadDelegate>(
                SoundOnLoadSignature, SoundOnLoadDetour);
            createFileHook = interop.HookFromImport<CreateFileWDelegate>(
                null, "KERNEL32.dll", "CreateFileW", 0, CreateFileWDetour);
            createFileHook.Enable();
            readSqPackHook.Enable();
            soundOnLoadHook.Enable();
            getResourceSyncHook.Enable();
            getResourceAsyncHook.Enable();
        }
        catch (Exception error)
        {
            unavailableReason = $"Resonance SCD resource hook unavailable: {error.Message}";
            readSqPackHook?.Dispose();
            soundOnLoadHook?.Dispose();
            createFileHook?.Dispose();
            getResourceSyncHook?.Dispose();
            getResourceAsyncHook?.Dispose();
        }
    }

    public bool IsAvailable => Volatile.Read(ref disposed) == 0
        && getResourceSyncHook is not null && getResourceAsyncHook is not null
        && readSqPackHook is not null && readFile is not null
        && soundOnLoadHook is not null && loadScdFileLocal is not null
        && Volatile.Read(ref unavailableReason) is null;
    public string? UnavailableReason => Volatile.Read(ref unavailableReason);

    public bool TryRegister(string virtualPath, string localPath, out string? error)
    {
        error = null;
        if (!IsAvailable)
        {
            error = UnavailableReason ?? "Resonance SCD resource hook unavailable";
            return false;
        }
        if (Path.IsPathRooted(virtualPath)
            || !virtualPath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Refusing non-Resonance resource override path: {virtualPath}";
            return false;
        }
        var fullPath = Path.GetFullPath(localPath);
        if (!File.Exists(fullPath) || fullPath.Length >= 260)
        {
            error = !File.Exists(fullPath)
                ? $"Generated SCD does not exist: {fullPath}"
                : "Generated SCD path exceeds FFXIV's local resource path limit";
            return false;
        }
        var key = Normalize(virtualPath);
        lock (mappingGate)
        {
            if (mappings.TryGetValue(key, out var existing))
            {
                if (!String.Equals(existing.LocalPath, fullPath, StringComparison.Ordinal))
                {
                    retiredPaths.Add(existing.Utf8Path);
                    mappings[key] = CreateMapping(fullPath, existing.References + 1);
                }
                else
                    mappings[key] = existing with { References = existing.References + 1 };
            }
            else
                mappings[key] = CreateMapping(fullPath, 1);
        }
        log.Information("Registered Resonance SCD override {VirtualPath} -> {LocalPath}", virtualPath, fullPath);
        return true;
    }

    public void Unregister(string virtualPath)
    {
        var key = Normalize(virtualPath);
        lock (mappingGate)
        {
            if (!mappings.TryGetValue(key, out var existing)) return;
            if (existing.References > 1)
                mappings[key] = existing with { References = existing.References - 1 };
            else
            {
                // GetResource copies this path into an asynchronously loaded handle. Keep the
                // backing storage alive until hook shutdown even after logical unregistration.
                retiredPaths.Add(existing.Utf8Path);
                mappings.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lock (mappingGate)
        {
            foreach (var mapping in mappings.Values) Marshal.FreeHGlobal(mapping.Utf8Path);
            foreach (var pointer in retiredPaths) Marshal.FreeHGlobal(pointer);
            mappings.Clear();
            retiredPaths.Clear();
        }
        readSqPackHook?.Disable();
        soundOnLoadHook?.Disable();
        createFileHook?.Disable();
        getResourceSyncHook?.Disable();
        getResourceAsyncHook?.Disable();
        readSqPackHook?.Dispose();
        soundOnLoadHook?.Dispose();
        createFileHook?.Dispose();
        getResourceSyncHook?.Dispose();
        getResourceAsyncHook?.Dispose();
        foreach (var pointer in createFilePath.Values) Marshal.FreeHGlobal(pointer);
        createFilePath.Dispose();
    }

    private byte SoundOnLoadDetour(
        FFXIVClientStructs.FFXIV.Client.System.Resource.Handle.ResourceHandle* handle,
        DescriptorOverlay* descriptor, byte unknown)
    {
        try
        {
            if (handle != null && IsMappedLocalPath(handle->FileName.ToString()))
            {
                log.Information("Loading Resonance SCD through FFXIV local sound loader {Path}",
                    handle->FileName.ToString());
                return loadScdFileLocal!(handle, descriptor, unknown);
            }
        }
        catch (Exception error)
        {
            Volatile.Write(ref unavailableReason, $"Resonance local SCD load failed: {error.Message}");
            log.Error(error, "Resonance local SCD load failed");
        }
        return soundOnLoadHook!.Original(handle, descriptor, unknown);
    }

    private bool IsMappedLocalPath(string path)
    {
        lock (mappingGate)
            return mappings.Values.Any(mapping => String.Equals(
                Normalize(mapping.LocalPath), Normalize(path), StringComparison.OrdinalIgnoreCase));
    }

    private nint GetResourceSyncDetour(nint resourceManager, nint category, nint resourceType,
        int* resourceHash, byte* path, nint parameters, byte* file, uint line)
    {
        var replacement = ResolveRequestedPath(path, resourceHash);
        return getResourceSyncHook!.Original(resourceManager, category, resourceType, resourceHash,
            replacement == null ? path : replacement, parameters, file, line);
    }

    private nint GetResourceAsyncDetour(nint resourceManager, nint category, nint resourceType,
        int* resourceHash, byte* path, nint parameters, byte hasHandleLock, byte* file, uint line)
    {
        var replacement = ResolveRequestedPath(path, resourceHash);
        return getResourceAsyncHook!.Original(resourceManager, category, resourceType, resourceHash,
            replacement == null ? path : replacement, parameters, hasHandleLock, file, line);
    }

    private byte* ResolveRequestedPath(byte* path, int* resourceHash)
    {
        if (path == null || Volatile.Read(ref disposed) != 0) return null;
        var length = 0;
        while (length < 4096 && path[length] != 0) length++;
        if (length == 4096) return null;
        var requested = System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(path, length));
        lock (mappingGate)
        {
            if (!mappings.TryGetValue(Normalize(requested), out var mapping)) return null;
            if (resourceHash != null)
                *resourceHash = unchecked((int)Lumina.Misc.Crc32.Get(
                    new ReadOnlySpan<byte>((void*)mapping.Utf8Path, mapping.Utf8Length)));
            log.Information("Redirecting Resonance resource identity {VirtualPath} -> {LocalPath}",
                requested, mapping.LocalPath);
            return (byte*)mapping.Utf8Path;
        }
    }

    private byte ReadSqPackDetour(nint resourceManager, DescriptorOverlay* descriptor, int priority, bool isSync)
    {
        try
        {
            if (descriptor != null && descriptor->ResourceHandle != null)
            {
                var gamePath = descriptor->ResourceHandle->FileName.ToString();
                Mapping? resolved = null;
                lock (mappingGate)
                    resolved = mappings.Values.FirstOrDefault(mapping =>
                        String.Equals(Normalize(mapping.LocalPath), Normalize(gamePath),
                            StringComparison.OrdinalIgnoreCase));
                if (resolved is not null)
                {
                    log.Information("Intercepted Resonance SCD resource read {GamePath} -> {LocalPath}",
                        gamePath, resolved.LocalPath);
                    descriptor->FileMode = GameFileMode.LoadUnpackedResource;
                    var storage = stackalloc char[0x11 + 0x0B + 14];
                    descriptor->FileInterface = (byte*)storage + 1;
                    WritePathToken(storage + 17, (byte*)resolved.Utf8Path, resolved.Utf8Length);
                    WritePathToken(&descriptor->FilePath, (byte*)resolved.Utf8Path, resolved.Utf8Length);
                    var result = readFile!(resourceManager, descriptor, priority, isSync ? (byte)1 : (byte)0);
                    log.Information("FFXIV local SCD read returned {Result} for {GamePath}", result, gamePath);
                    return result;
                }
            }
        }
        catch (Exception error)
        {
            Volatile.Write(ref unavailableReason,
                $"Resonance SCD resource redirect failed: {error.Message}");
            log.Error(error, "Resonance SCD resource redirect failed");
        }
        return readSqPackHook!.Original(resourceManager, descriptor, priority, isSync);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private static Mapping CreateMapping(string localPath, int references)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(localPath);
        var pointer = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        Marshal.WriteByte(pointer, bytes.Length, 0);
        return new Mapping(localPath, pointer, bytes.Length, references);
    }

    private nint CreateFileWDetour(char* fileName, uint access, uint shareMode, nint security,
        uint creation, uint flags, nint template)
    {
        if (!TryReadPathToken(fileName, out var utf8Path))
            return createFileHook!.Original(fileName, access, shareMode, security, creation, flags, template);

        log.Information("Decoded Resonance local resource path in CreateFileW");
        var destination = (char*)createFilePath.Value;
        destination[0] = '\\';
        destination[1] = '\\';
        destination[2] = '?';
        destination[3] = '\\';
        var written = System.Text.Encoding.UTF8.GetChars(utf8Path, new Span<char>(destination + 4, 260));
        for (var index = 0; index < written; index++)
            if (destination[index + 4] == '/') destination[index + 4] = '\\';
        destination[written + 4] = '\0';
        return createFileHook!.Original(destination, access, shareMode, security, creation, flags, template);
    }

    private static void WritePathToken(char* buffer, byte* address, int length)
    {
        buffer[0] = PathTokenPrefix;
        var bytes = (byte*)buffer;
        new Span<byte>(bytes + 2, 23).Fill(0xFF);
        var pointer = (ulong)address;
        for (var index = 0; index < 8; index++) bytes[2 + index * 2] = (byte)(pointer >> (index * 8));
        var count = (uint)length;
        for (var index = 0; index < 4; index++) bytes[18 + index * 2] = (byte)(count >> (index * 8));
        bytes[26] = 0;
        bytes[27] = 0;
    }

    private static bool TryReadPathToken(char* buffer, out ReadOnlySpan<byte> path)
    {
        if (buffer == null || buffer[0] != PathTokenPrefix)
        {
            path = [];
            return false;
        }
        var bytes = (byte*)buffer;
        ulong pointer = 0;
        for (var index = 0; index < 8; index++) pointer |= (ulong)bytes[2 + index * 2] << (index * 8);
        uint length = 0;
        for (var index = 0; index < 4; index++) length |= (uint)bytes[18 + index * 2] << (index * 8);
        path = new ReadOnlySpan<byte>((void*)pointer, checked((int)length));
        return true;
    }
}

public sealed class FfxivClientSoundPlayer : IGameMixerSoundPlayer
{
    private readonly IFramework framework;

    public FfxivClientSoundPlayer(IFramework framework)
        => this.framework = framework ?? throw new ArgumentNullException(nameof(framework));

    public System.Threading.Tasks.Task<nint> PlayAsync(string virtualPath, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        return framework.RunOnFrameworkThread(() =>
        {
            token.ThrowIfCancellationRequested();
            return PlayOnFramework(virtualPath);
        });
    }

    public System.Threading.Tasks.Task StopAsync(nint playback)
    {
        if (playback == 0) return System.Threading.Tasks.Task.CompletedTask;
        if (framework.IsFrameworkUnloading) return System.Threading.Tasks.Task.CompletedTask;
        return framework.RunOnFrameworkThread(() => StopOnFramework(playback));
    }

    private static unsafe void StopOnFramework(nint playback)
    {
        var framework = Framework.Instance();
        var sound = framework == null ? null : framework->SoundManager;
        if (sound == null) return;
        var current = sound->ActiveSoundDataListHead;
        for (var index = 0; current != null && index < 256; index++)
        {
            var next = (SoundData*)current->ISoundData.Next;
            // Validate pointer identity against the live list before touching
            // it; SoundData is game-owned and may already have been freed.
            if ((nint)current == playback)
            {
                current->ISoundData.Stop(0);
                return;
            }
            current = next;
        }
    }

    private static unsafe nint PlayOnFramework(string virtualPath)
    {
        var framework = Framework.Instance();
        var sound = framework == null ? null : framework->SoundManager;
        if (sound == null) throw new InvalidOperationException("FFXIV SoundManager is unavailable");

        var path = Marshal.StringToCoTaskMemUTF8(virtualPath);
        try
        {
            var playback = sound->PlayCutsceneVoSound((byte*)path);
            if (playback == null)
                throw new InvalidOperationException("FFXIV SoundManager rejected the Resonance SCD");
            return (nint)playback;
        }
        finally { Marshal.FreeCoTaskMem(path); }
    }
}
