using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

/// <summary>
/// The commander's run is still going (ET-440): the start's own payload, repeated while it runs, so a member who was
/// not there at the start — joined the fleet later, declined the offer, or was cut off by a discard — can still join.
/// Its own type rather than a repeat of fleet.run-group, which every client reads as a fresh start and offers again;
/// an older client or server drops it unread.
/// </summary>
public sealed class FleetRunRunningEvent(RunGroupCodeStart data, int? characterId = null)
    : IntegrationEvent<RunGroupCodeStart>(data, characterId), IFleetScopedEvent
{
    public override string EventType => "fleet.run-group.running";

    public long FleetId => Data.FleetId;
}
