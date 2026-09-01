using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Events;

public sealed class RunSavedEvent(Guid runId) : IntegrationEvent<Guid>(runId)
{
    public override string EventType => "runs.saved";
}
