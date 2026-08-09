using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace Resonance.Audio;

public sealed class GameVolumeService
{
    private readonly IGameConfig gameConfig;

    public GameVolumeService(IGameConfig gameConfig) => this.gameConfig = gameConfig;

    public unsafe float GetVoiceGain(float pluginVolume)
    {
        gameConfig.System.TryGetBool("IsSndMaster", out var masterMuted);
        gameConfig.System.TryGetBool("IsSndVoice", out var voiceMuted);
        if (masterMuted || voiceMuted) return 0f;

        var framework = Framework.Instance();
        var sound = framework == null ? null : framework->SoundManager;
        if (sound == null) return Math.Clamp(pluginVolume, 0f, 2f);
        return Math.Clamp(sound->MasterVolume * sound->GetEffectiveVolume(SoundBus.Voice) * pluginVolume, 0f, 2f);
    }
}
