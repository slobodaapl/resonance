using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace Resonance.Game;

public sealed class CutsceneDetector : IDisposable
{
    private static readonly ConditionFlag[] CutsceneFlags =
    [
        ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.WatchingCutscene78,
    ];

    private readonly ICondition condition;
    public bool IsInCutscene { get; private set; }
    public event Action? Started;
    public event Action? Ended;

    public CutsceneDetector(ICondition condition)
    {
        this.condition = condition;
        IsInCutscene = condition.Any(CutsceneFlags);
        condition.ConditionChange += OnConditionChange;
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (!CutsceneFlags.Contains(flag)) return;
        var current = condition.Any(CutsceneFlags);
        if (current == IsInCutscene) return;
        IsInCutscene = current;
        if (current) Started?.Invoke(); else Ended?.Invoke();
    }

    public void Dispose() => condition.ConditionChange -= OnConditionChange;
}

