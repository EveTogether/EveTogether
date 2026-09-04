using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>One activity, fully expanded — the deelruns behind an <c>ActivitySummary</c> row, each with its own
/// loot, bounty and enemy rows. Reads the runs the summary was built from directly, never through a lookup that
/// guesses which run is meant (ET-160).</summary>
public sealed record GetActivityDetailQuery(Guid ActivitySummaryId) : IQuery<Result<ActivityDetailDto>>;
