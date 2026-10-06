using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunLootEntry
{
    public Guid Id { get; set; }
    public Guid RunLootCaptureId { get; set; }
    public int ItemTypeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public long? Quantity { get; set; }
    public decimal? Volume { get; set; }
    public decimal? ClipboardPrice { get; set; }
    public LootKind LootKind { get; set; }

    /// <summary>What one unit was worth when the line was priced (ET-463) — the figure every valuation reads, so a run
    /// keeps the value it had when the loot came in. Null until a price is known; until then the line is valued at the
    /// live cache price and said to be.</summary>
    public decimal? UnitPriceIsk { get; set; }

    public DateTime? PricedAtUtc { get; set; }
    public PriceSnapshotSource? PriceSource { get; set; }
    public RunLootCapture? RunLootCapture { get; set; }
}
