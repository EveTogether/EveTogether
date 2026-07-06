using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Dialogs;

/// <summary>
/// The values entered in the invite dialog: which connected character to invite, the role to grant on
/// accept, and an optional free-text message that rides along on the invite. (Wing/squad placement is decided by
/// the owner from the roster after the invitee joins — <c>CreateInvite</c> carries only role + message.)
/// </summary>
public sealed record FleetInviteResult(
    int InviteeCharacterId,
    FleetRole Role,
    string? Message);
