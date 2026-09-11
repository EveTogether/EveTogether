namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>
/// The catalogue key a run's TYPE is shown under — in the run window, the detail screen and the runs overview alike
/// (ET-226). Deliberately not the six scanner groups (Combat/Data/Relic/Gas/Ore Site, Wormhole): those are one
/// source among several (<see cref="RunTypeResolver"/>), and a mission, a mining run with no site and a homefront
/// (ET-228) all need their own entry here without the scanner ever having a word for them.
///
/// Stored nowhere — resolved fresh from <see cref="ActivityKind"/> and <see cref="Entities.Run.SignatureGroupSnapshot"/>
/// every time it is shown, so a member appended here (a new site kind, ET-229's Mining detection, ET-228's Homefront
/// detection) changes what existing rows resolve to without a migration. Appended only, like <see cref="ActivityKind"/>
/// and <see cref="SiteTypeSource"/> beside it — never reordered, never renumbered.
/// </summary>
public enum RunTypeId
{
    /// <summary>An <see cref="ActivityKind.Site"/> run whose scanner group was never recorded or never recognised —
    /// a manual start, or a run saved before this catalogue existed. Shown as "Site", never guessed into a specific
    /// kind (ET-226 AC-3).</summary>
    Unknown,
    CombatSite,
    DataSite,
    RelicSite,
    GasSite,
    OreSite,
    Wormhole,
    Mission,

    /// <summary>Mining with no site under it — an asteroid belt, started by hand. Reserved here for ET-229, which
    /// adds the detection; this catalogue only lays the entry down (AGENTS.md §1, "reserve ≠ build").</summary>
    Mining,

    /// <summary>A site whose archetype makes it one of CCP's Homefront dungeons, with its own kind (Raid, Metaliminal
    /// Meteoroid, …) besides. Reserved for ET-228, which adds the archetype-based detection this catalogue does not
    /// build.</summary>
    Homefront,
    Abyssal
}
