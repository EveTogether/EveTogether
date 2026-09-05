using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>Sum of bounty ISK across saved, non-deleted activities since <paramref name="SinceUtc"/>, for the given
/// characters — what "today" actually banked, read from storage rather than a live tracker's lifetime running total
/// (ET-195). Loot is out of scope here; the caller decides whether an estimated price belongs next to a received
/// amount at all.</summary>
public sealed record GetIskTodayQuery(DateTime SinceUtc, IReadOnlyList<long> CharacterIds) : IQuery<Result<decimal>>;
