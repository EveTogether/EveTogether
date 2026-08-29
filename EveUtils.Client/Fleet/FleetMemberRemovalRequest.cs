namespace EveUtils.Client.Fleet;

/// <summary>
/// One pilot to remove, as the calling screen knows them. <paramref name="MemberId"/> identifies the roster row to
/// remove; <paramref name="CharacterId"/> is what ESI keys an in-game kick by; <paramref name="FleetId"/> is the fleet
/// they are leaving, which is what stops this client publishing their metrics for it. The coupled in-game ids come
/// from the fleet header the screen already holds — both null means "not coupled", which is the case where the second
/// confirmation never appears.
/// </summary>
public sealed record FleetMemberRemovalRequest(
    long FleetId,
    long MemberId,
    int CharacterId,
    string MemberName,
    string FleetName,
    long? EsiFleetId = null,
    int? EsiFleetBossId = null);
