using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class UnrecognisedLootLineInput
{
    public required string Name { get; init; }
    public required long Quantity { get; init; }
    public long? CharacterId { get; init; }
    public required UnrecognisedItemSource Source { get; init; }
    public required DateTime FirstSeenAtUtc { get; init; }
    public required UnrecognisedItemStatus Status { get; init; }
    public DateTime? ResolvedAtUtc { get; init; }
    public int? ResolvedTypeId { get; init; }
    public decimal? ResolvedUnitPrice { get; init; }
}
