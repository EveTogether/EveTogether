using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// Wire payload of a fleet invite, pushed to the invitee and returned by the create handler. Carries
/// the fleet name so the invitee can show "invited to &lt;fleet&gt;" without a second lookup.
/// </summary>
public sealed record FleetInvitePayload(
    long InviteId,
    long FleetId,
    string FleetName,
    int InviterCharacterId,
    int InviteeCharacterId,
    FleetRole Role);
