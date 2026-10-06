using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

public sealed class FleetRunSavedEvent(RunGroupSave data, int? characterId = null)
    : IntegrationEvent<RunGroupSave>(data, characterId), IFleetScopedEvent
{
    public override string EventType => "fleet.run-saved";

    public long FleetId => Data.FleetId;
}
