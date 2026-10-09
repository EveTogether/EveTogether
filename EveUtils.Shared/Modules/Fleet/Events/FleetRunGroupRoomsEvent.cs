using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

public sealed class FleetRunGroupRoomsEvent(RunGroupRooms data, int? characterId = null)
    : IntegrationEvent<RunGroupRooms>(data, characterId), IFleetScopedEvent, IServerAttributedEvent
{
    public override string EventType => "fleet.run-group.rooms";

    public long FleetId => Data.FleetId;
}
