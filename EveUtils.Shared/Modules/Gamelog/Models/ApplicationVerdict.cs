namespace EveUtils.Shared.Modules.Gamelog.Models;

/// <summary>
/// How well a weapon's shots are landing on what it is shooting now (ET-277). Three of these are readings of a
/// measured percentage; the other three say why there is no percentage, which a screen must show differently from a
/// low one.
/// </summary>
public enum ApplicationVerdict
{
    /// <summary>Not shooting.</summary>
    Idle = 0,

    /// <summary>Shooting, but too few shots at the current target to judge — right after a target switch, say.</summary>
    NotEnoughShots = 1,

    /// <summary>Nothing to judge this weapon's shots by: an unknown weapon that only writes "Hits", or a missile shot at
    /// a player ship, whose resists are unknown.</summary>
    NotMeasurable = 2,

    /// <summary>Landing badly: grazes, glances and misses. Change range, speed or target.</summary>
    Adjust = 3,

    Ok = 4,

    /// <summary>Landing well: mostly solid hits.</summary>
    SweetSpot = 5,

    /// <summary>A missile with no full volley to measure against yet (ET-282): no fit known and nothing learned. A
    /// client from before this value reads it as idle.</summary>
    Learning = 6,
}
