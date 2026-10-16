using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The never-saved runs thrown away since <paramref name="SinceUtc"/>, latest first — what the Undo toast offered
/// for a few seconds, kept reachable after it is gone (ET-511).</summary>
public sealed record GetRecentlyDiscardedRunsQuery(DateTime SinceUtc) : IQuery<Result<IReadOnlyList<RecentlyDiscardedRunDto>>>;
