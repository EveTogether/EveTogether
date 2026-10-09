namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// One section of the run window or the activity detail screen (ET-236). A run type names the sections it has in
/// <see cref="RunTypeCatalogue"/>; <see cref="RunSectionModules"/> says what each one is on each screen and in which
/// order they stand. Never stored, so a member can be added anywhere — the screen order is the registry's, not this.
/// </summary>
public enum RunSectionId
{
    Activity,
    Mission,
    Enemies,
    Fit,
    Fleet,
    Bounty,
    Loot,
    Escalation,
    Consumables,
    Mining,

    /// <summary>A homefront's attendance list — who was in the site at completion (ET-230).</summary>
    Homefront,

    /// <summary>The own losses linked to the activity's runs (ET-331).</summary>
    Loss,

    /// <summary>What the fleet of a group run destroyed, information only and never part of TOTAL ISK (ET-373).</summary>
    FleetKills,

    /// <summary>What the pilot dealt, took and was neuted for over the run, as tiles and a chart, from the stored combat (ET-468).</summary>
    Combat,

    /// <summary>Per room the time, idle, damage, spawn HP, share, overkill and loot, on the group's one room set (ET-469).</summary>
    Rooms,

    /// <summary>The enemies of each abyssal room in the order to shoot them, live only (ET-369).</summary>
    Targets,

    /// <summary>How the pilot's shots landed and what the enemy's did, per target and weapon, from the stored hit tallies (ET-474).</summary>
    HitQuality
}
