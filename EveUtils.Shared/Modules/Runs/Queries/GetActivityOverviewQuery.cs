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
    long? CharacterId = null,
    /// <summary>Only activities whose group code was minted for this fleet (<c>RunGroupOrigin</c>, ET-182). A solo
    /// run never matches, since it has no group code and therefore no recorded fleet.
    ///
    /// ⚠️ An empty result does not mean the fleet flew nothing — it means nothing it flew is <em>known</em> to be
    /// this fleet's. <c>RunGroupOrigin</c> only holds codes minted from the moment this filter shipped; a fleet
    /// whose runs all predate that has no rows to find here, same as one that truly flew none. A caller that shows
    /// this as a count or a list must not read "empty" as "zero" for a fleet old enough to predate the table,
    /// exactly the distinction ET-166 already draws by leaving its completed-run count out rather than showing a
    /// false zero.</summary>
    long? FleetId = null) : IQuery<Result<IReadOnlyList<ActivityOverviewRowDto>>>;
