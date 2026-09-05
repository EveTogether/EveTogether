using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>Who has a run in this activity right now, on the same definition <c>ActivitySummary.ParticipantCount</c>
/// uses (ET-131): the runs sharing <paramref name="GroupCode"/>, or just <paramref name="RunId"/> when it is flown
/// alone. Reads <c>Run</c> directly rather than <c>ActivitySummary</c>, because the activity window asks this
/// before a save — and often before there is one to ask — exists.</summary>
public sealed record GetRunGroupParticipantsQuery(string? GroupCode, Guid RunId)
    : IQuery<Result<IReadOnlyList<RunGroupParticipantDto>>>;
