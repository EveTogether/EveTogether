using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

/// <summary>The unit price one type of a loss linked to a run is valued at (ET-464), fixed the way
/// <see cref="RunLootEntry.UnitPriceIsk"/> is (ET-463). One row per type per link: the quantities stay on the killmail,
/// and a loss moved to another run is priced afresh there rather than carrying this run's price along. Client only, as
/// the losses are.</summary>
public sealed class RunLossPrice
{
    public Guid RunId { get; set; }
    public int CharacterId { get; set; }
    public int KillmailId { get; set; }
    public int TypeId { get; set; }

    /// <summary>Null while the cache had no price for the type; the hourly refresh fills it once.</summary>
    public decimal? UnitPriceIsk { get; set; }

    public DateTime? PricedAtUtc { get; set; }
    public PriceSnapshotSource? PriceSource { get; set; }
}
