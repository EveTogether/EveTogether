using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

// One pilot's own leg of a run whose clock is per pilot started again after their own STOP (ET-250) — the others'
// FLEET line reads them as "in" again within seconds instead of waiting out the twenty-minute cut-off. Its own type
// rather than a repeat of fleet.run-group.pilot-stopped or fleet.run-group: an older client or an un-rebuilt server
// drops the unknown type instead of misreading a resume as something else. The character is the event's own.
public sealed class FleetRunPilotResumedEvent(RunGroupResume data, int? characterId = null)
    : IntegrationEvent<RunGroupResume>(data, characterId), IFleetScopedEvent
{
    public override string EventType => "fleet.run-group.pilot-resumed";

    public long FleetId => Data.FleetId;
}
