using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Events;

/// <summary>A run's clock went to rest, or picked back up from rest — <see cref="Commands.SetRunStoppedCommand"/> is
/// one command for both directions, so this is one event for both: a listener only needs to know that whatever it
/// last read about which runs are running is stale, not which of the two happened.</summary>
public sealed class RunRunningStateChangedEvent(Guid runId) : IntegrationEvent<Guid>(runId)
{
    public override string EventType => "runs.runningStateChanged";
}
