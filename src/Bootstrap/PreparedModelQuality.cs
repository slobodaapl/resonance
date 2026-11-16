using Resonance.Plugin;

namespace Resonance.Bootstrap;

internal sealed record PreparedModelQuality(
    QualityPreset Quality,
    string BasePath,
    string CodecPath,
    string? DesignPath,
    string ModelHash,
    string DesignHash);
