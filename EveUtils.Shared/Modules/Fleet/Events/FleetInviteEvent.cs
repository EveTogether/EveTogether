using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

/// <summary>
/// Pushed to a single character when they are invited to a fleet. Targeted: the server reroutes
/// it only to the invitee's connections. The durable <see cref="Entities.FleetInvite"/> is the source of truth;
/// this event is the live notification (and is re-sent on attach if still Pending).
/// </summary>
public sealed class FleetInviteEvent(FleetInvitePayload data, int? characterId = null)
    : IntegrationEvent<FleetInvitePayload>(data, characterId), ITargetedEvent
{
    public override string EventType => "fleet.invite";

    public int TargetCharacterId => Data.InviteeCharacterId;
}
