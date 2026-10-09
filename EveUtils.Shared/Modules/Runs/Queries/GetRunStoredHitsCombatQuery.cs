using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>ET-470: a run's pilot's stored hits inside its window, through the SAVE's build and written nowhere; null without any.</summary>
public sealed record GetRunStoredHitsCombatQuery(Guid RunId) : IQuery<Result<RunCombatTimelineDto?>>;
