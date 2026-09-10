using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Events;

/// <summary>A soft-deleted run was undone (ET-214) and its activity's summary has already been rebuilt by the time
/// this fires. Same reasoning as <see cref="RunDeletedEvent"/>, the other direction.</summary>
public sealed class RunRestoredEvent(Guid runId) : IntegrationEvent<Guid>(runId)
{
    public override string EventType => "runs.restored";
}
