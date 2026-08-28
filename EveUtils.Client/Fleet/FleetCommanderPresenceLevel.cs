namespace EveUtils.Client.Fleet;

/// <summary>
/// How a <see cref="FleetCommanderPresence"/> reads at a glance — the single input the fleet-metrics badge
/// colours on. An intermediate step (e.g. amber from a share of the fleet) is a new member here plus its style,
/// not a new decision spread over the view.
/// </summary>
public enum FleetCommanderPresenceLevel
{
    /// <summary>The fleet has no commander, or the commander shares no location: show a neutral badge, no ratio.</summary>
    Unknown = 0,

    /// <summary>Part of the fleet stands with the commander.</summary>
    Partial = 1,

    /// <summary>Every tracked member stands in the commander's system.</summary>
    Complete = 2
}
