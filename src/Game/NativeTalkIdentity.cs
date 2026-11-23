using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Base;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.STD;
using FFXIVClientStructs.FFXIV.Client.Sound;
using FFXIVClientStructs.FFXIV.Client.UI;
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace Resonance.Game;

public sealed unsafe class NativeTalkIdentity : IDisposable
{
    private const string ScriptSignature = "40 57 41 56 48 81 EC 18 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 F0 00 00 00 4D 8B F0 48 8B F9";
    private const string SceneSignature = "40 55 57 41 54 41 55 41 56 48 81 EC 30 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 10 01 00 00 4C 8B A4 24 80 01 00 00 49 8B F9";
    private const string QueueSignature = "40 53 48 83 EC 30 48 83 79 68 00 48 8B D9 75 08 32 C0 48 83 C4 30 5B C3";
    private const string AdvanceSignature = "48 89 5C 24 18 56 48 81 EC 80 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 70 8B 81 C0 05 00 00 48 8B F1 39 81 E0 0D 00 00";
    private const string ClipSignature = "40 57 41 56 48 83 EC 58 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 40 0F B6 81 88 00 00 00 48 8B F9 C0 E8 04 A8 01";
    private const string ControllerSignature = "48 83 EC 28 E8 ?? ?? ?? ?? 48 85 C0 74 15 48 8B 80 C8 00 00 00 48 85 C0 74 09 48 8B 40 08 48 83 C4 28 C3";
    private const string ControllerResourceSignature = "48 89 5C 24 10 57 48 83 EC 20 48 8B 99 ?? ?? ?? ?? 48 8B FA 48 85 DB 74 ?? 48 83 B9 ?? ?? ?? ?? 00 75 ?? 8B 83 ?? ?? ?? ?? D1 E8 A8 01";
    private const string ResourceDataSignature = "48 89 5C 24 08 57 48 83 EC 20 4C 8B 41 ?? 45 33 C9 41 8B C1 48 8B FA 48 8B D9 F0 41 0F C0 80 ?? ?? ?? ?? 3C 02";
    private delegate void ScriptTalk(nint owner, byte success, nint text, ulong parameter);
    private delegate void SceneTalk(nint owner, nint data, nint text, nint actor, nint parameter);
    private delegate byte QueuedTalk(nint owner);
    private delegate void ClipTalk(nint clip);

    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private readonly DialogueIdentity identity = new();
    private Hook<ScriptTalk>? scriptHook;
    private Hook<SceneTalk>? sceneHook;
    private Hook<QueuedTalk>? queuedHook;
    private Hook<ClipTalk>? clipHook;
    private nint dispatchingClip;
    private int disposed;
    private delegate* unmanaged<nint, byte> advanceTalk;
    private delegate* unmanaged<nint, CutSceneController*> getController;
    private int controllerResourceOffset;
    private delegate* unmanaged<nint, uint*, byte*> getResourceData;

    public NativeTalkIdentity(IAddonLifecycle lifecycle, IGameInteropProvider interop,
        ISigScanner scanner, IPluginLog log)
    {
        this.lifecycle = lifecycle;
        this.log = log;
        try
        {
            advanceTalk = (delegate* unmanaged<nint, byte>)(void*)scanner.ScanText(AdvanceSignature);
            getController = (delegate* unmanaged<nint, CutSceneController*>)(void*)scanner.ScanText(ControllerSignature);
            var resourceAccessor = scanner.ScanText(ControllerResourceSignature)
                - scanner.Module.BaseAddress + scanner.SearchBase;
            // Decode mov rbx, [rcx+disp32] from the scan image; the SDK's field layout differs.
            controllerResourceOffset = *(int*)(resourceAccessor + 13);
            getResourceData = (delegate* unmanaged<nint, uint*, byte*>)(void*)scanner.ScanText(ResourceDataSignature);
            scriptHook = interop.HookFromSignature<ScriptTalk>(ScriptSignature, OnScriptTalk);
            sceneHook = interop.HookFromSignature<SceneTalk>(SceneSignature, OnSceneTalk);
            queuedHook = interop.HookFromSignature<QueuedTalk>(QueueSignature, OnQueuedTalk);
            clipHook = interop.HookFromSignature<ClipTalk>(ClipSignature, OnClipTalk);
            lifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", OnPresentation);
            lifecycle.RegisterListener(AddonEvent.PostRefresh, "Talk", OnPresentation);
            lifecycle.RegisterListener(AddonEvent.PreFinalize, "Talk", OnFinalize);
            scriptHook.Enable();
            sceneHook.Enable();
            queuedHook.Enable();
            clipHook.Enable();
            log.Information("Native Talk actor capture enabled for scripted and scheduled dialogue");
        }
        catch (Exception error)
        {
            try { Dispose(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Native Talk actor capture cleanup failed", error, cleanupError);
            }
            log.Warning(error, "Native Talk actor capture unavailable; using dialogue fallback");
        }
    }

    public DialoguePresentation? Read(nint addon, (string Speaker, string Text) content) => identity.Read(addon, content);
    public bool? TryAdvance(nint addon) => advanceTalk == null ? null : advanceTalk(addon) != 0;

    private void OnClipTalk(nint clip)
    {
        var previous = dispatchingClip;
        try
        {
            dispatchingClip = clip;
            clipHook!.Original(clip);
        }
        finally { dispatchingClip = previous; }
    }

    private static (DialogueVoice Voice, bool Playing)? ReadVoice(nint handle)
    {
        var framework = NativeFramework.Instance();
        var sound = framework == null ? null : framework->SoundManager;
        if (sound == null || handle == 0) return null;
        var current = sound->ActiveSoundDataListHead;
        for (var index = 0; current != null && index < 256; index++)
        {
            if ((nint)current == handle)
            {
                var path = current->ISoundData.GetFileName().ToString();
                return String.IsNullOrEmpty(path) ? null
                    : (new(handle, path), !current->ISoundData.GetIsLoadingSoundResource()
                        && current->ISoundData.IsPlaying());
            }
            current = (SoundData*)current->ISoundData.Next;
        }
        return null;
    }

    public bool IsVoicePlaying(DialogueVoice voice) =>
        ReadVoice(voice.Handle) is { Playing: true } current && current.Voice == voice;

    private DialogueRow? ReadRow(nint data)
    {
        var controller = getController(dispatchingClip);
        return controller == null ? null
            : NativeCutsceneRow.Read((nint)controller, controllerResourceOffset, controller->CutsceneId, data, getResourceData);
    }

    private static DialogueActor? Snapshot(GameObject* actor) =>
        actor == null || actor->BaseId == 0 || (byte)actor->ObjectKind is not (2 or 3)
            ? null
            : new(actor->BaseId, actor->GetGameObjectId().Id, (nint)actor, (byte)actor->ObjectKind);

    private void OnScriptTalk(nint owner, byte success, nint text, ulong parameter)
    {
        DialogueActor? actor = null;
        try { actor = Snapshot(*(GameObject**)(owner + 0x48)); }
        catch (Exception error) { log.Warning(error, "Script Talk actor snapshot failed"); }
        identity.Dispatch(actor, () => scriptHook!.Original(owner, success, text, parameter));
    }

    private void OnSceneTalk(nint owner, nint data, nint text, nint actor, nint parameter)
    {
        var queue = (StdDeque<nint>*)(owner + 0x48);
        var before = queue->Count;
        DialogueActor? snapshot = null;
        DialogueRow? row = null;
        DialogueVoice? nativeVoice = null;
        try
        {
            // Scheduler CharacterObject vfunc 19 returns its live GameObject.
            snapshot = actor == 0 ? null
                : Snapshot(((delegate* unmanaged<nint, GameObject*>) (*(nint**)actor)[19])(actor));
            identity.RetainQueued(owner, queue->ToArray().ToHashSet());
            if (dispatchingClip != 0 && *(int*)(dispatchingClip + 0x84) == 0x26
                && *(nint*)(dispatchingClip + 0x48) == data)
            {
                row = ReadRow(data);
                var ownerVoice = *(nint*)(dispatchingClip + 0xb8);
                if (ownerVoice != 0) nativeVoice = ReadVoice(*(nint*)(ownerVoice + 8))?.Voice;
            }
        }
        catch (Exception error) { log.Warning(error, "Scheduled Talk actor snapshot failed"); }
        identity.Dispatch(snapshot, () => sceneHook!.Original(owner, data, text, actor, parameter), row, nativeVoice);
        if (queue->Count > before) identity.Queue(owner, (*queue)[queue->Count - 1], snapshot, row, nativeVoice);
    }

    private byte OnQueuedTalk(nint owner)
    {
        var queue = (StdDeque<nint>*)(owner + 0x48);
        var source = queue->Count == 0 ? null : identity.TakeQueued(owner, (*queue)[0]);
        byte result = 0;
        identity.Dispatch(source?.Actor, () => result = queuedHook!.Original(owner), source?.Row, source?.NativeVoice);
        return result;
    }

    private void OnPresentation(AddonEvent _, AddonArgs args)
    {
        var (count, address) = args switch
        {
            AddonSetupArgs setup => (setup.AtkValueCount, setup.AtkValues),
            AddonRefreshArgs refresh => (refresh.AtkValueCount, refresh.AtkValues),
            _ => (0u, (nint)0),
        };
        if (count == 0 || address == 0) return;
        var type = ((AtkValue*)address)->Type & AtkValueType.TypeMask;
        if (type is AtkValueType.String or AtkValueType.ConstString)
            identity.Present(args.Addon.Address, TalkObserver.ReadContent((AddonTalk*)args.Addon.Address));
    }

    private void OnFinalize(AddonEvent _, AddonArgs args) => identity.Finalize(args.Addon.Address);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var failures = new List<Exception>();
        void Attempt(Action cleanup)
        {
            try { cleanup(); }
            catch (Exception error) { failures.Add(error); }
        }
        Attempt(() => clipHook?.Disable());
        Attempt(() => queuedHook?.Disable());
        Attempt(() => sceneHook?.Disable());
        Attempt(() => scriptHook?.Disable());
        Attempt(() => clipHook?.Dispose());
        Attempt(() => queuedHook?.Dispose());
        Attempt(() => sceneHook?.Dispose());
        Attempt(() => scriptHook?.Dispose());
        Attempt(() => lifecycle.UnregisterListener(OnPresentation));
        Attempt(() => lifecycle.UnregisterListener(OnFinalize));
        identity.Clear();
        advanceTalk = null;
        getController = null;
        controllerResourceOffset = 0;
        getResourceData = null;
        dispatchingClip = 0;
        if (failures.Count != 0) throw new AggregateException("Native Talk actor capture cleanup failed", failures);
    }
}
