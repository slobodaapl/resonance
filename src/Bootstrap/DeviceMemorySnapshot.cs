using Resonance.Tts;

namespace Resonance.Bootstrap;

public sealed record DeviceMemorySnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<BackendInfo> Devices,
    string? ResidentBackendName,
    string? ResidentPhysicalDeviceId,
    long ResidentWeightsBytes)
{
    public bool IncludesModel(BackendInfo device) => ResidentWeightsBytes > 0
        && (ResidentBackendName == device.Name
            || ResidentPhysicalDeviceId is not null && ResidentPhysicalDeviceId == device.PhysicalDeviceId);

    public bool CanDetermineInclusion(BackendInfo device) => ResidentWeightsBytes <= 0 || IncludesModel(device)
        || ResidentPhysicalDeviceId is not null && device.PhysicalDeviceId is not null;
}
