using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>
/// The combat a run kept at SAVE (ET-467), or null for a run saved before it was kept.
/// </summary>
public sealed record GetRunCombatTimelineQuery(Guid RunId) : IQuery<Result<RunCombatTimelineDto?>>;
