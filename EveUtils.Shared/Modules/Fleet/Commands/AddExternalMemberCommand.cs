using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Adds an external EVE character — one with no client session on this server — directly to a fleet on trust
/// . The mirror of <see cref="CreateFleetInviteCommand"/> without the invite round-trip: the owner
/// vouches for the character, so they become a default-accepted member immediately. No invite, no message.
/// Creator-only via the owning fleet (the FC/WC/SC seam activates once the roster exists); <c>fleet.invite</c>
/// is gated server-side. Returns the new roster member's id.
/// </summary>
[RequiresPermission(FleetPermissions.Invite)]
public sealed record AddExternalMemberCommand(long FleetId, int CharacterId, int ActingCharacterId)
    : ICommand<Result<long>>;
