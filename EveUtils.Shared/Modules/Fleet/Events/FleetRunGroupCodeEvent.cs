using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

public sealed class FleetRunGroupCodeEvent(RunGroupCodeStart data, int? characterId = null)
    : IntegrationEvent<RunGroupCodeStart>(data, characterId), IFleetScopedEvent
{
    public override string EventType => "fleet.run-group";

    public long FleetId => Data.FleetId;
}
