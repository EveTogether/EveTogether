namespace EveUtils.Client.Fleet;

/// <summary>Client-side view of a pending request-to-join (gRPC <c>JoinRequestDto</c>), for the owner's roster
/// pending-section.</summary>
public sealed record FleetJoinRequestInfo(
    long Id,
    long FleetId,
    int RequesterCharacterId);
