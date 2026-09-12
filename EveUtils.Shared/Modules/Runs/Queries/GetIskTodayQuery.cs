using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>Sum of TOTAL ISK across saved, non-deleted activities since <paramref name="SinceUtc"/>, for the given
/// characters, read from storage rather than a live tracker's lifetime running total (ET-195). The same total each
/// activity shows on the runs overview and its detail screen (ET-256) — bounty alone left an evening's mission
/// rewards and loot out of "today" while every other screen counted them.</summary>
public sealed record GetIskTodayQuery(DateTime SinceUtc, IReadOnlyList<long> CharacterIds) : IQuery<Result<decimal>>;
