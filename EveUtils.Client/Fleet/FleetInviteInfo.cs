using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Fleet;

/// <summary>Client-side view of a pending fleet invite (gRPC <c>InviteDto</c>), for the on-attach durable sync.</summary>
public sealed record FleetInviteInfo(
    long Id,
    long FleetId,
    int InviterCharacterId,
    int InviteeCharacterId,
    FleetRole Role,
    FleetInviteStatus Status);
