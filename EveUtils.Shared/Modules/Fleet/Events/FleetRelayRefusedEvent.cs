using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

/// <summary>
/// The server telling one client that what it sends into a fleet reaches nobody, and why (ET-440). Sent once per
/// connection, fleet and reason. Its own type, so a client from before it drops it unread.
/// </summary>
public sealed class FleetRelayRefusedEvent(FleetRelayRefusedPayload data, int? characterId = null)
    : IntegrationEvent<FleetRelayRefusedPayload>(data, characterId)
{
    public override string EventType => "fleet.relay-refused";
}
