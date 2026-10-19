using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Shared.Modules.RunOutcomes.Queries;

/// <summary>The shared outcomes of each of <paramref name="Keys"/> completed on or after <paramref name="Since"/>
/// (at most 365 days back), weighed by the per-pilot cap within that window. The server does not compute the figures: a
/// client values the loot at today's price and runs the statistic itself. Refused with <c>PERMISSION_DENIED</c> while
/// the server's switch is off.</summary>
public sealed record GetRunOutcomesQuery(IReadOnlyList<ActivityStatsKey> Keys, DateOnly Since)
    : IQuery<Result<IReadOnlyList<ActivityOutcomesDto>>>;
