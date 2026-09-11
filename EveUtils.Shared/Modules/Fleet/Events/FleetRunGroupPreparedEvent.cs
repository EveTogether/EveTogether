using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

// The commander set a run up and nobody is in it yet (ET-246): the same offer as fleet.run-group, carried on the same
// payload, under a type of its own. Its own type is the whole point — a client or server that predates it drops an
// event type it cannot read, where a "prepared" flag on fleet.run-group would reach an older member as a start and put
// his clock on the moment the commander picked a tier. StartedAtUtc is the moment it was prepared; nobody's clock
// starts from it.
public sealed class FleetRunGroupPreparedEvent(RunGroupCodeStart data, int? characterId = null)
    : IntegrationEvent<RunGroupCodeStart>(data, characterId), IFleetScopedEvent
{
    public override string EventType => "fleet.run-group.prepared";

    public long FleetId => Data.FleetId;
}
