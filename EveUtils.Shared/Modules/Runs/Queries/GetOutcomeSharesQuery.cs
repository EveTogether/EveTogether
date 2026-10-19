using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>The activities as they would be shared right now (ET-515): the ones named in
/// <paramref name="ActivityRefs"/>, each answered even when it is no longer shareable, or with it null every activity
/// with a key that completed since <paramref name="SinceUtc"/> — what a backfill reads.</summary>
public sealed record GetOutcomeSharesQuery(DateTime SinceUtc, IReadOnlyCollection<string>? ActivityRefs = null)
    : IQuery<Result<IReadOnlyList<ActivityOutcomeShareDto>>>;
