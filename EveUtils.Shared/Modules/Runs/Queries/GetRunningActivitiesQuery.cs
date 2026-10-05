using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>Each activity on the clock and what it has earned so far, read off storage the way an unfinished run's
/// TOTAL ISK is (<see cref="GetUnfinishedRunsQuery"/>): bounty is written as it comes in (ET-219) and loot as it is
/// captured, so nothing here depends on a run window being open. One row per <c>GroupCode ?? RunId</c>, like the
/// overview. <see cref="GetRunningRunsQuery"/> names the runs themselves.</summary>
public sealed record GetRunningActivitiesQuery : IQuery<Result<IReadOnlyList<RunningActivityDto>>>;
