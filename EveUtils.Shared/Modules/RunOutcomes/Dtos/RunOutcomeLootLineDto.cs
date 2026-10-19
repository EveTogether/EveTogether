using EveUtils.Shared.Modules.RunOutcomes.Enums;

namespace EveUtils.Shared.Modules.RunOutcomes.Dtos;

/// <summary>One item type of a shared outcome, per type merged: no name, price or time.</summary>
public sealed class RunOutcomeLootLineDto
{
    public required RunOutcomeLootKind Kind { get; init; }
    public required int TypeId { get; init; }
    public required long Quantity { get; init; }
}
