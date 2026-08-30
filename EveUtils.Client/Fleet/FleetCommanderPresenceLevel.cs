namespace EveUtils.Client.Fleet;

/// <summary>
/// How a <see cref="FleetCommanderPresence"/> reads at a glance — the single input the fleet-metrics badge
/// colours on. An intermediate step (e.g. amber from a share of the fleet) is a new member here plus its style,
/// not a new decision spread over the view.
/// </summary>
public enum FleetCommanderPresenceLevel
{
    /// <summary>
    /// The fleet has no commander, the commander shares no location, or no member's location is known at all:
    /// show a neutral badge, no ratio. The last of those is the one that has to be said out loud — a fleet nobody
    /// has a location for counts 0 of 0, and "all of the ones we know about" would otherwise read as complete.
    /// </summary>
    Unknown = 0,

    /// <summary>Part of the fleet stands with the commander.</summary>
    Partial = 1,

    /// <summary>Every member whose location is known stands in the commander's system.</summary>
    Complete = 2
}
