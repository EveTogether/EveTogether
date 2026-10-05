using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <param name="ActivityKey">The runs' group code, or the run id where it has none.</param>
/// <param name="SolarSystemId">Where its earliest run was started, where that was recorded.</param>
/// <param name="Kills">The bounty lines its runs hold, one per NPC kill; a kill that paid no bounty is not counted.</param>
/// <param name="LootIskNet">Null while none of its captured loot has a known price.</param>
public sealed record RunningActivityDto(string ActivityKey, int? SolarSystemId, decimal BountyIsk, int Kills, decimal? LootIskNet, IskBreakdown Isk);
