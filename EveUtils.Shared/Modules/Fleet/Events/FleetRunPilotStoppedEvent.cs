using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

// One pilot's own leg of a run whose clock is per pilot came to rest (ET-243) — the commander's included. Not
// fleet.run-stopped: that one tells every member to stop, and an older member still obeys it, so a pilot's own exit
// travels under a type such a client does not read. The character is the event's own.
public sealed class FleetRunPilotStoppedEvent(RunGroupStop data, int? characterId = null)
    : IntegrationEvent<RunGroupStop>(data, characterId), IFleetScopedEvent
{
    public override string EventType => "fleet.run-group.pilot-stopped";

    public long FleetId => Data.FleetId;
}
