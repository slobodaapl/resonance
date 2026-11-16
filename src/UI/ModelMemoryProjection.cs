namespace Resonance.UI;

internal readonly record struct ModelMemoryProjection(
    double Total, double Other, double Model, double Free, double Overflow)
{
    internal double Projected => Other + Model;
    internal bool NearCapacity => Total > 0 && Projected / Total >= .9;

    internal static ModelMemoryProjection Create(ulong total, ulong free, long includedWeights, long selectedWeights)
    {
        var used = (double)(total - Math.Min(free, total));
        var other = Math.Max(0, used - Math.Max(0, includedWeights));
        var model = Math.Max(0, selectedWeights);
        return new(total, other, model, Math.Max(0, total - other - model), Math.Max(0, other + model - total));
    }
}
