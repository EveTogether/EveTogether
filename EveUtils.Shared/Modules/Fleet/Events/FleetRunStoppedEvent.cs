using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

public sealed class FleetRunStoppedEvent(RunGroupStop data, int? characterId = null)
    : IntegrationEvent<RunGroupStop>(data, characterId), IFleetScopedEvent
{
    public override string EventType => "fleet.run-stopped";

    public long FleetId => Data.FleetId;
}
