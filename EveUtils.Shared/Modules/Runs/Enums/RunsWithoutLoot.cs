namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>What the earnings statistic does with a run whose loot was never counted (<see cref="LootBasis.None"/>,
/// ET-513, Jithran 18-10). Either way the runs are reported, so a screen can say how many were left out.</summary>
public enum RunsWithoutLoot
{
    /// <summary>Counted as a run without loot: a mission, where the reward carries the value.</summary>
    Counted,

    /// <summary>Left out: a site, where uncounted loot would read as a run that brought nothing.</summary>
    Excluded
}
