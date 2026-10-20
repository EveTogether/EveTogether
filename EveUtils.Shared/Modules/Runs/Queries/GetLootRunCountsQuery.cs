using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>For every activity the pilot has flown, how many of its runs a loot table counts (ET-521): the "n runs with
/// loot" the site picker shows beside each site. An activity with none is not in the answer.</summary>
public sealed record GetLootRunCountsQuery : IQuery<Result<IReadOnlyDictionary<ActivityStatsKey, int>>>;
