using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The runs behind many activities at once, as thin as a publish needs (ET-295): no loot, bounty, enemy
/// observations or pricing, unlike <see cref="GetActivityDetailQuery"/> — publishing all of a day's or a view's local
/// activities reads every one of their runs in a single round trip instead of one detail read per activity.</summary>
public sealed record GetActivityRunsForPublishQuery(IReadOnlyList<Guid> SummaryIds) : IQuery<Result<IReadOnlyList<ActivityRunForPublishDto>>>;
