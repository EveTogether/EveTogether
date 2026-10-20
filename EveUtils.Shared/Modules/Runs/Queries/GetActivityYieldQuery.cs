using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The yield of one saved activity, <paramref name="ActivityRef"/> being its group code, else its run id
/// (ET-517). Null when the activity has no statistic key (mining, a free run, an unnamed site).</summary>
public sealed record GetActivityYieldQuery(string ActivityRef) : IQuery<Result<ActivityYieldDto?>>;
