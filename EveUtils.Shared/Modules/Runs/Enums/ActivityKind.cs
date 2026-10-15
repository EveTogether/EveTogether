namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>
/// What kind of activity a run is. Carried from wherever the run is started, never worked out again further down —
/// deriving it from "is it abyssal" is what kept <see cref="Mission"/> unreachable and any seventh kind out (ET-174).
///
/// Stored as its int, so members are only ever appended and never reordered. Every kind here is equal: none of them
/// is the default the others are measured against.
/// </summary>
public enum ActivityKind
{
    Abyssal,
    Site,
    Mission,

    /// <summary>A mining run with no site behind it at all (ET-229, ET-265) — the manual-start dialog is the only
    /// caller that resolves to this today, since nothing yet detects a live mining activity on its own.</summary>
    Mining,

    /// <summary>A freerun (ET-508): no site, no mission, no pocket — a roam, ESS or officer hunting, anything none of
    /// the kinds above covers. It records whatever happens from start to STOP and is named after its start moment.</summary>
    Free
}
