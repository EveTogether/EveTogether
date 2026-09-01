using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

public sealed class FleetRunDiscardedEvent(RunGroupDiscard data, int? characterId = null)
    : IntegrationEvent<RunGroupDiscard>(data, characterId), IFleetScopedEvent
{
    public override string EventType => "fleet.run-discarded";

    public long FleetId => Data.FleetId;
}
