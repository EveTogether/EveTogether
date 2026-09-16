using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>When the oldest saved activity started, or null when nothing has been saved yet — where the runs
/// overview's activity strip (ET-292) stops saying "no runs" and starts saying "before EVE Together tracked runs".
/// One <c>MIN</c> over <c>ActivitySummary.StartedAtUtc</c>, answered off its own index.</summary>
public sealed record GetFirstActivityStartQuery : IQuery<Result<DateTime?>>;
