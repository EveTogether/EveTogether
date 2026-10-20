namespace EveUtils.Client.ViewModels.Activity;

/// <summary>What the EXPECTED EARNINGS panel of the run window shows (ET-516).</summary>
public enum ExpectedEarningsState
{
    /// <summary>No key to look up (mining, a freerun, an unnamed site): the panel is not on screen at all.</summary>
    Hidden,

    /// <summary>The key is known and the figures are on their way.</summary>
    Loading,

    /// <summary>A typical figure with its range.</summary>
    Figures,

    /// <summary>Fewer runs than the minimum: no number, only how far off it is.</summary>
    TooFewRuns
}
