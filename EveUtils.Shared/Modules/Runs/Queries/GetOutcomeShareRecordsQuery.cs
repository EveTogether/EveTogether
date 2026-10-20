using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>Where this client stands with its shared outcomes (ET-515): of <paramref name="ServerAddress"/> alone, or
/// of every server when it is null.</summary>
public sealed record GetOutcomeShareRecordsQuery(string? ServerAddress = null)
    : IQuery<Result<IReadOnlyList<OutcomeShareRecordDto>>>;
