using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.RunOutcomes.Dtos;

/// <summary>One shared outcome as any pilot on the server may read it (ET-514): no id, pilot, bucket, name or place.
/// <see cref="Weight"/> stands in for the pilot: the per-pilot cap the server applied, 0 for an outcome the
/// statistic leaves out.</summary>
public sealed class RunOutcomeDto
{
    public required DateOnly CompletedOn { get; init; }
    public int? DurationSeconds { get; init; }
    public required decimal FixedIsk { get; init; }
    public required int FleetSize { get; init; }
    public required LootBasis LootBasis { get; init; }
    public required int OpenUnrecognisedLootCount { get; init; }
    public required double Weight { get; init; }
}
