using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Invites a character to a fleet. Creator-only via the owning fleet (the FC/WC/SC seam activates
/// once the roster exists); <c>fleet.invite</c> is gated server-side. Persists a durable Pending invite
/// and returns its payload so the transport can push a targeted <c>FleetInviteEvent</c> to the invitee.
/// </summary>
[RequiresPermission(FleetPermissions.Invite)]
public sealed record CreateFleetInviteCommand(
    long FleetId,
    int InviteeCharacterId,
    FleetRole Role,
    long? WingId,
    long? SquadId,
    string? Message,
    int ActingCharacterId) : ICommand<Result<FleetInvitePayload>>;
