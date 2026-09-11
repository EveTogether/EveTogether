using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

public sealed class FleetRunGroupAbyssalUpdatedEvent(RunGroupAbyssalUpdate data, int? characterId = null)
    : IntegrationEvent<RunGroupAbyssalUpdate>(data, characterId), IFleetScopedEvent
{
    public override string EventType => "fleet.run-group.abyssal-updated";

    public long FleetId => Data.FleetId;
}
