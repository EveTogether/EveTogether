using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Enums;

namespace EveUtils.Shared.Modules.RunOutcomes.Events;

/// <summary>
/// The server's shared run outcomes changed (ET-514). Published on the local bus once the write is done; nothing relays
/// it yet, since clients read the outcomes when they compute a figure. Carries no character: the outcomes are anonymous
/// and the signal stays that way.
/// </summary>
/// <param name="outcomeId">The outcome a single change was about; null for a withdrawal or a prune.</param>
public sealed class RunOutcomesChangedEvent(RunOutcomesChangeKind kind, int count, long? outcomeId = null)
    : IntegrationEvent<RunOutcomesChangedData>(new RunOutcomesChangedData(kind, count, outcomeId))
{
    public override string EventType => "runoutcomes.changed";
}

/// <param name="Count">How many outcomes the change touched.</param>
public sealed record RunOutcomesChangedData(RunOutcomesChangeKind Kind, int Count, long? OutcomeId);
