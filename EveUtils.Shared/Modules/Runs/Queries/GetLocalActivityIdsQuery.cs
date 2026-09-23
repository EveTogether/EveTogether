using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>Every saved activity one of these characters flew that no run of it was ever published from (ET-324) —
/// the runs overview's "local" (a row with no server sync state), over all time rather than the month in view, so the
/// home can say how many runs live only on this PC and publish exactly those.</summary>
public sealed record GetLocalActivityIdsQuery(IReadOnlyList<long> CharacterIds) : IQuery<Result<IReadOnlyList<Guid>>>;
