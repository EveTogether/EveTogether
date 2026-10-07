using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One name the log holds (ET-460), added up over every run and input it was entered through. The resolved
/// fields are only set for a name that has been recognised since.</summary>
public sealed class UnrecognisedLootItemDto
{
    public required string Name { get; init; }
    public required long TotalQuantity { get; init; }
    public required int RunCount { get; init; }
    public required DateTime FirstSeenAtUtc { get; init; }
    public required IReadOnlyList<UnrecognisedItemSource> Sources { get; init; }
    public DateTime? ResolvedAtUtc { get; init; }
    public int? ResolvedTypeId { get; init; }
    public decimal? ResolvedUnitPrice { get; init; }
}
