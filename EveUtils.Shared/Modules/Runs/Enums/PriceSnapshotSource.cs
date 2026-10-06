namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>How a line's fixed unit price came to be (ET-463). The price itself never moves with the market once set;
/// this says which moment it was taken at.</summary>
public enum PriceSnapshotSource
{
    /// <summary>Taken when the line was stored — the price the loot, ore or filament was worth as it came in.</summary>
    Capture,

    /// <summary>The line had no price when it was stored and got the first one the cache had afterwards, once.</summary>
    Backfill,

    /// <summary>Replaced on purpose by "Re-value at current prices", writing over the historic value.</summary>
    Revalued,

    /// <summary>Set by the migration that introduced snapshots: the cache price of that day, since the real one at
    /// capture is not known any more.</summary>
    Migrated
}
