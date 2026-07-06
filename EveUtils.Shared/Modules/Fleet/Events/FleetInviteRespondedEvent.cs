using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

/// <summary>
/// Pushed back to the inviter when the invitee accepts or denies. Targeted at the inviter's
/// connections so they see the outcome without polling.
/// </summary>
public sealed class FleetInviteRespondedEvent(FleetInviteResponsePayload data, int? characterId = null)
    : IntegrationEvent<FleetInviteResponsePayload>(data, characterId), ITargetedEvent
{
    public override string EventType => "fleet.invite.responded";

    public int TargetCharacterId => Data.InviterCharacterId;
}
