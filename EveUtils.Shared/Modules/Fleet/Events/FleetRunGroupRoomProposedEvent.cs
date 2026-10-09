using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

public sealed class FleetRunGroupRoomProposedEvent(RunGroupRoomProposal data, int? characterId = null)
    : IntegrationEvent<RunGroupRoomProposal>(data, characterId), IFleetScopedEvent, IServerAttributedEvent
{
    public override string EventType => "fleet.run-group.room-proposed";

    public long FleetId => Data.FleetId;
}
