namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>How much a loot table, or one of its items, may say with numbers (ET-519). The thresholds are those of the
/// earnings statistic, counted on the effective number of runs.</summary>
public enum LootTableConfidence
{
    /// <summary>Too few runs: the names of what was seen, no numbers.</summary>
    None,

    /// <summary>"Seen in 9 of 12": counts of runs, never a percentage.</summary>
    Seen,

    /// <summary>Enough runs for a drop rate as a percentage.</summary>
    Rated
}
