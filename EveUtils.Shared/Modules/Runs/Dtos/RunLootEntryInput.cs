using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class RunLootEntryInput
{
    public required int ItemTypeId { get; init; }
    public required string Name { get; init; }
    public long? Quantity { get; init; }
    public decimal? Volume { get; init; }
    public decimal? ClipboardPrice { get; init; }
    public required LootKind LootKind { get; init; }

    // The fixed price travels (ET-463), so a server copy and every fleetmate value the line the same. Not required: a
    // payload from an older client reads as "no price fixed yet".
    public decimal? UnitPriceIsk { get; init; }
    public DateTime? PricedAtUtc { get; init; }
    public PriceSnapshotSource? PriceSource { get; init; }
}
