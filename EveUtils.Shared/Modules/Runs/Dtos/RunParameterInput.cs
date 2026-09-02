using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class RunParameterInput
{
    public required RunParameterKey ParameterKey { get; init; }
    public required string TypedValue { get; init; }
    public decimal? Amount { get; init; }
    public int? ItemTypeId { get; init; }
    public int? BonusWindowSeconds { get; init; }
    public required DateTime ObservedAtUtc { get; init; }
}
