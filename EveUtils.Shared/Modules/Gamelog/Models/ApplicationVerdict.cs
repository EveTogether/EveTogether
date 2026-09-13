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

    /// <summary>The log carries no hit quality for this weapon: missiles always write "Hits", however they land.</summary>
    NotMeasurable = 2,

    /// <summary>Landing badly: grazes, glances and misses. Change range, speed or target.</summary>
    Adjust = 3,

    Ok = 4,

    /// <summary>Landing well: mostly solid hits.</summary>
    SweetSpot = 5,
}
