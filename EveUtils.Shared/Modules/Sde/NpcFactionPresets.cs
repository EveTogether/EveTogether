using EveUtils.Shared.Modules.Dogma;

namespace EveUtils.Shared.Modules.Sde;

/// <summary>
/// SDE-derived damage profiles for the ~12 major NPC factions. Each entry maps a display name to the
/// InvGroup name LIKE pattern used to aggregate damage attributes across all NPC types in that faction
/// (categoryId 11, NOT filtered by published). Weights are derived live from the SDE; these static fallbacks
/// match the golden profiles in the plan and are used in unit tests.
/// </summary>
public static class NpcFactionPresets
{
    /// <summary>Ordered list of display-name / group-name-LIKE pairs for the faction preset dropdown.</summary>
    public static readonly IReadOnlyList<(string Name, string GroupLike)> FactionPatterns =
    [
        ("Guristas",        "%Guristas%"),
        ("Angel Cartel",    "%Angel%"),
        ("Sansha's Nation", "%Sansha%"),
        ("Blood Raiders",   "%Blood Raider%"),
        ("Serpentis",       "%Serpentis%"),
        ("Triglavian",      "%Triglavian%"),
        ("Sleepers",        "%Sleeper%"),
        ("Rogue Drones",    "%Rogue Drone%"),
        ("Amarr Empire",    "%Amarr%"),
        ("Caldari State",   "%Caldari%"),
        ("Gallente Federation", "%Gallente%"),
        ("Minmatar Republic",   "%Minmatar%"),
    ];

    /// <summary>Static golden profiles matching the SDE-derived aggregates (used as unit test expectations).
    /// Keys match the <c>Name</c> entries in <see cref="FactionPatterns"/>.</summary>
    public static readonly IReadOnlyDictionary<string, DamageProfile> GoldenProfiles =
        new Dictionary<string, DamageProfile>
        {
            ["Guristas"]        = new DamageProfile(Em: 0,    Th: 0.46, Kin: 0.54, Exp: 0),
            ["Angel Cartel"]    = new DamageProfile(Em: 0.15, Th: 0.08, Kin: 0.40, Exp: 0.36).Normalized(),
            ["Sansha's Nation"] = new DamageProfile(Em: 0.56, Th: 0.44, Kin: 0,    Exp: 0),
            ["Blood Raiders"]   = new DamageProfile(Em: 0.55, Th: 0.44, Kin: 0,    Exp: 0).Normalized(),
            ["Serpentis"]       = new DamageProfile(Em: 0,    Th: 0.54, Kin: 0.46, Exp: 0),
            ["Triglavian"]      = new DamageProfile(Em: 0,    Th: 0.57, Kin: 0,    Exp: 0.43),
            ["Sleepers"]        = new DamageProfile(Em: 0.50, Th: 0.50, Kin: 0,    Exp: 0),
            ["Rogue Drones"]    = new DamageProfile(Em: 0.46, Th: 0.31, Kin: 0.13, Exp: 0.10).Normalized(),
        };
}
