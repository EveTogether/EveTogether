using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>One row per activity, newest first — reads <c>ActivitySummary</c> as it stands rather than re-deriving
/// the grouping from <c>Run</c>, since the summary already is "one row per activity" (<c>GroupCode ?? RunId</c>).
/// Only saved, non-deleted activities show up here, because that is all <c>ActivitySummary</c> ever holds; a
/// running run needs its own band elsewhere (ET-160).</summary>
public sealed record GetActivityOverviewQuery(
    int Page = 0,
    int PageSize = 50,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    /// <summary>Only activities this character actually flew a run in — not just any activity in the window.</summary>
    long? CharacterId = null) : IQuery<Result<IReadOnlyList<ActivityOverviewRowDto>>>;
