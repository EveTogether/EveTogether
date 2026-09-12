namespace EveUtils.Shared.Modules.Runs;

/// <summary>
/// CCP's 24 Homefront Operation dungeons — SDE archetype 70, measured against build 3503375 and recorded in Depot
/// <c>domain/homefronts.md</c> §2: nine kinds, six with one variant per empire (or, for Metaliminal Meteoroid, per
/// ore) and three with a single site. Fixed CCP content, so <see cref="RunTypeResolver"/> reads this table by
/// dungeon id rather than asking the SDE's own archetype column live — the same reasoning
/// <see cref="RunTypeResolver"/>'s own scanner-group text match already follows for the six ordinary site kinds.
///
/// The kind (ET-228) is read off this table wherever it is shown; it is never stored on a run, so there is no
/// second copy of a fact <see cref="Entities.Run.SiteTypeId"/> already carries.
/// </summary>
public static class HomefrontCatalogue
{
    public static IReadOnlyDictionary<int, string> KindByDungeonId { get; } = new Dictionary<int, string>
    {
        // Combat, 5 pilots.
        [10347] = "Raid", [10377] = "Raid", [10378] = "Raid", [10379] = "Raid",
        [10320] = "Dread Assault", [10381] = "Dread Assault", [10383] = "Dread Assault", [10384] = "Dread Assault",
        [10238] = "Emergency Aid", [10373] = "Emergency Aid", [10376] = "Emergency Aid", [10380] = "Emergency Aid",
        [10309] = "Suspicious Signal", [10367] = "Suspicious Signal", [10368] = "Suspicious Signal", [10369] = "Suspicious Signal",
        // Mining, 5 pilots.
        [10312] = "Metaliminal Meteoroid", [10387] = "Metaliminal Meteoroid",
        [10388] = "Metaliminal Meteoroid", [10389] = "Metaliminal Meteoroid",
        [10346] = "Abyssal Artifact Recovery",
        // 3 pilots, one site each.
        [10713] = "Salvage Research",
        [10714] = "Stabilize Rift",
        [10705] = "Traffic Stop"
    };

    public static bool IsHomefrontDungeonId(int dungeonId) => KindByDungeonId.ContainsKey(dungeonId);

    /// <summary>Whether a homefront kind is flown as mining rather than combat or salvage (domain/homefronts.md §2)
    /// — the per-run refinement ET-236's design left for ET-228: a mining homefront's catalogue row claims MINING
    /// besides the six sections every site already has.</summary>
    public static bool IsMiningKind(string kind) => kind is "Metaliminal Meteoroid" or "Abyssal Artifact Recovery";

    /// <summary>The one mining kind whose site capacity is a fixed, known number: a Metaliminal Meteoroid is always a
    /// single 5,000-unit asteroid (domain/homefronts.md §2 and §6.1, CCP 23.02 — measured against seven real sites,
    /// mined units plus residue add up to exactly 5,000 every time). Abyssal Artifact Recovery mines 9 waves of 12
    /// asteroids each, not one comparably simple figure, so it is left out here — ET-234 shows AAR a fleet total with
    /// no "remaining" line, the same as an ordinary mining fleet with no known capacity at all.</summary>
    public static IReadOnlyDictionary<string, int> CapacityUnitsByKind { get; } = new Dictionary<string, int>
    {
        ["Metaliminal Meteoroid"] = 5000
    };
}
