using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// The invitee accepts or denies an invite. No app-permission gate — responding is the invitee's own
/// action; authorization is "acting character == invitee" in the handler. On accept the invitee joins the
/// roster. Returns the response payload so the transport can push <c>FleetInviteRespondedEvent</c> to the inviter.
/// </summary>
public sealed record RespondToFleetInviteCommand(long InviteId, bool Accept, int ActingCharacterId)
    : ICommand<Result<FleetInviteResponsePayload>>;
