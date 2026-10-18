namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Which spread around the typical figure the run count supports (ET-513): an empirical P10-P90 only covers
/// about 80% of future runs from around 20 runs, so below that the band is the middle half.</summary>
public enum EarningsBand
{
    None,

    /// <summary>P25-P75, "half of runs".</summary>
    Quartiles,

    /// <summary>P10-P90, "8 of 10 runs".</summary>
    Deciles
}
