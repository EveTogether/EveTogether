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
}
