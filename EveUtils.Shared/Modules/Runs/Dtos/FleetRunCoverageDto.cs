namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>How many of a fleet's runs are known to be completed, and whether that number can be trusted (ET-185).
/// <see cref="IsKnown"/> is false when <c>CompletedCount</c> is zero for a reason that is not "this fleet flew
/// nothing" — the fleet predates <c>RunGroupOrigin</c> (ET-182) and an empty result there is silent, not a fact. A
/// caller must never print <see cref="CompletedCount"/> when <see cref="IsKnown"/> is false.</summary>
public sealed record FleetRunCoverageDto(int CompletedCount, bool IsKnown);
