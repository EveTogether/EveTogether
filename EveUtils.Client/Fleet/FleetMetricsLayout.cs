namespace EveUtils.Client.Fleet;

/// <summary>
/// How the fleet-metrics window lays its member rows out — the one input the view switches its item template and
/// panel on. Ordered from most detail per member to most members per screen; a fourth density is a new member here
/// plus its template, not a new decision spread over the view.
/// </summary>
public enum FleetMetricsLayout
{
    /// <summary>One full-width row per member: every figure plus the live graph. The default.</summary>
    List = 0,

    /// <summary>Cards side by side: every live figure plus the graph, one size down. No bounty.</summary>
    Grid = 1,

    /// <summary>One thin line per member: every live figure, no graph and no bounty.</summary>
    Compact = 2
}
