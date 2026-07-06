namespace EveUtils.Client.Esi;

/// <summary>
/// Per-pilot outcome of an ESI fleet invite. Acceptance/decline happens in-game afterwards and is observed
/// through the roster sync (the pilot appears in the live roster), not at invite time — so this only records whether the
/// invitation was sent or rejected by ESI. Distinct from <c>FleetInviteStatus</c> (our internal EVE Together invites).
/// </summary>
public enum EsiInviteStatus
{
    /// <summary>The invitation was sent; the pilot must still accept it in-game.</summary>
    Invited,

    /// <summary>ESI rejected the invite (e.g. a CSPA charge on the target, or the structure isn't pushed yet).</summary>
    Failed,
}
