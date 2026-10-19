using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Shared.Modules.RunOutcomes.Queries;

/// <summary>The loot lines of <paramref name="Key"/>'s shared outcomes since <paramref name="Since"/>, each with the
/// weight <see cref="GetRunOutcomesQuery"/> gives it: read only when a loot table opens (ET-519), since one site
/// carries dozens of lines per outcome. None below <see cref="RunOutcomesModule.MinimumPilots"/> contributing pilots.
/// Refused with <c>PERMISSION_DENIED</c> while the server's switch is off.</summary>
public sealed record GetRunOutcomeLootQuery(ActivityStatsKey Key, DateOnly Since) : IQuery<Result<ActivityOutcomeLootDto>>;
