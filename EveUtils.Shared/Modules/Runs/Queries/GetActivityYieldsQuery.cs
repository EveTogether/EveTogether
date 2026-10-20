using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The yields of several saved activities at once, by activity reference (ET-521): what the runs list sets
/// against each activity's typical run. An activity with no statistic key, or older than the widest statistic window, is
/// not in the answer. The most valuable drop is not worked out here; <see cref="GetActivityYieldQuery"/> has it.</summary>
public sealed record GetActivityYieldsQuery(IReadOnlyCollection<string> ActivityRefs)
    : IQuery<Result<IReadOnlyDictionary<string, ActivityYieldDto>>>;
