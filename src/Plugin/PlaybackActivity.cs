namespace Resonance.Plugin;

public readonly record struct PlaybackActivity(bool Speaking, bool Generating, bool Paused, float? OutputPeak);
