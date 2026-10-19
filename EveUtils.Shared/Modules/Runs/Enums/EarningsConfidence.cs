namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>How far the effective run count carries the earnings figures (ET-513).</summary>
public enum EarningsConfidence
{
    /// <summary>Below the minimum: no figure at all, only how many runs there are.</summary>
    None,

    /// <summary>A typical figure with the half-of-runs band, not yet the 8-of-10 band.</summary>
    Low,

    /// <summary>Enough runs for the 8-of-10 band.</summary>
    Normal
}
