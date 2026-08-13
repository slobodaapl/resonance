using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Resonance.Bootstrap;
using Resonance.Plugin;
using Resonance.Scheduling;

namespace Resonance.Ipc;

public sealed class IpcService : IDisposable
{
    public const int ApiVersion = 1;
    private readonly ICallGateProvider<int> apiVersion;
    private readonly ICallGateProvider<bool> isReady;
    private readonly ICallGateProvider<bool> isSpeaking;
    private readonly ICallGateProvider<string> backendInfo;
    private readonly ICallGateProvider<string, string?> speakerProfile;
    private readonly ICallGateProvider<long, string, string, object?> lineStarted;
    private readonly ICallGateProvider<long, string, string, object?> lineFinished;
    private readonly ICallGateProvider<string, object?> nativeVoiceObserved;
    private readonly ICallGateProvider<string, string, object?> speakerProfileUpgraded;
    private readonly SessionCoordinator coordinator;

    public IpcService(IDalamudPluginInterface plugin, BootstrapService bootstrap, SessionCoordinator coordinator)
    {
        this.coordinator = coordinator;
        apiVersion = plugin.GetIpcProvider<int>("Resonance.ApiVersion");
        isReady = plugin.GetIpcProvider<bool>("Resonance.IsReady");
        isSpeaking = plugin.GetIpcProvider<bool>("Resonance.IsSpeaking");
        backendInfo = plugin.GetIpcProvider<string>("Resonance.GetBackendInfo");
        speakerProfile = plugin.GetIpcProvider<string, string?>("Resonance.GetSpeakerProfile");
        lineStarted = plugin.GetIpcProvider<long, string, string, object?>("Resonance.LineStarted");
        lineFinished = plugin.GetIpcProvider<long, string, string, object?>("Resonance.LineFinished");
        nativeVoiceObserved = plugin.GetIpcProvider<string, object?>("Resonance.NativeVoiceObserved");
        speakerProfileUpgraded = plugin.GetIpcProvider<string, string, object?>("Resonance.SpeakerProfileUpgraded");

        apiVersion.RegisterFunc(() => ApiVersion);
        isReady.RegisterFunc(() => bootstrap.IsReady);
        isSpeaking.RegisterFunc(() => coordinator.IsSpeaking);
        backendInfo.RegisterFunc(() => JsonSerializer.Serialize(bootstrap.RuntimeManager?.Selection));
        speakerProfile.RegisterFunc(coordinator.GetSpeakerProfile);
        coordinator.LineStarted += OnLineStarted;
        coordinator.LineFinished += OnLineFinished;
        coordinator.SpeakerProfileUpgraded += OnProfileUpgraded;
    }

    private void OnLineStarted(DubLine line) => lineStarted.SendMessage(line.Sequence, line.SpeakerKey, line.Text);
    private void OnLineFinished(DubLine line) => lineFinished.SendMessage(line.Sequence, line.SpeakerKey, line.Text);
    private void OnProfileUpgraded(string speakerKey, string profileId) => speakerProfileUpgraded.SendMessage(speakerKey, profileId);

    public void Dispose()
    {
        coordinator.LineStarted -= OnLineStarted;
        coordinator.LineFinished -= OnLineFinished;
        coordinator.SpeakerProfileUpgraded -= OnProfileUpgraded;
        apiVersion.UnregisterFunc();
        isReady.UnregisterFunc();
        isSpeaking.UnregisterFunc();
        backendInfo.UnregisterFunc();
        speakerProfile.UnregisterFunc();
        lineStarted.UnregisterAction();
        lineFinished.UnregisterAction();
        nativeVoiceObserved.UnregisterAction();
        speakerProfileUpgraded.UnregisterAction();
    }
}
