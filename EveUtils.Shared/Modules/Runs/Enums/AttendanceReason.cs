namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>
/// Why a character stands ticked or unticked on a homefront's attendance list (ET-230). Everything but
/// <see cref="SetByHand"/> is a proposal from evidence — never a verdict: the pilot who dealt damage and warped out
/// before the end stands proposed and has to be unticked, the one who sat idle in the site the other way round.
/// Stored with the list, so an entry names why without having to rebuild the evidence. Appended only.
/// </summary>
public enum AttendanceReason
{
    /// <summary>Nothing in the evidence for this character — the hauler outside the site.</summary>
    NoActivityLogged,

    /// <summary>At least 1,000 outgoing damage, the threshold EVE's own rule names (domain/homefronts.md §4.1).</summary>
    DamageDealt,

    /// <summary>At least 1,000 remote repair given, the same threshold.</summary>
    RemoteRepair,

    /// <summary>Remote capacitor given. Whether EVE counts it is open (V5), so it only ever proposes.</summary>
    RemoteCapacitor,

    /// <summary>At least one successful salvage — Salvage Research's own interaction.</summary>
    Salvaged,

    /// <summary>At least one mining cycle (ET-229) — measured to count at a Metaliminal Meteoroid.</summary>
    Mined,

    /// <summary>A character of another pilot whose client reported damage or mining on this run — all this client can
    /// see of someone else's gamelog.</summary>
    FleetActivity,

    /// <summary>Carried over from the list the previous homefront of the same fleet ended with.</summary>
    SameAsLastSite,

    /// <summary>The one who decides set it against the proposal.</summary>
    SetByHand
}
