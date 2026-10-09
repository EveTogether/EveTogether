using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

/// <summary>
/// The server telling the clients of a character's fleet mates that the character connected or disconnected (ET-492).
/// The roster carries the same flag, but only as it stood when it was read: without this a fleet screen kept calling a
/// mate offline until it was reopened. Its own type, so a client from before it drops it unread.
/// </summary>
public sealed class FleetMateConnectionEvent(FleetMateConnectionPayload data, int? characterId = null)
    : IntegrationEvent<FleetMateConnectionPayload>(data, characterId)
{
    public override string EventType => "fleet.mate-connection";
}
