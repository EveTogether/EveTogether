namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>How firm one contribution to TOTAL ISK is (ET-256). Stored by number beside it.</summary>
public enum IskCertainty
{
    /// <summary>Money that arrived, or loot valued at a known price.</summary>
    Measured = 0,

    /// <summary>Owed but not paid yet — a homefront payout (ET-231). It counts towards the total, and the total says
    /// it holds such a part.</summary>
    Expected = 1,

    /// <summary>Something is there that nobody can value yet — loot of which not one line has a price. Counts as
    /// nothing, and is never shown as a zero.</summary>
    Unknown = 2
}
