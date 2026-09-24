using System;
using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Shared.Modules.Sde.Storage;

/// <summary>An abyssal NPC's faction. <see cref="Mixed"/> is a reading, not a guess: two or more recognised
/// factions among a set of names report this rather than picking one.</summary>
public enum AbyssalNpcFaction
{
    Triglavian,
    RogueDrones,
    Sleepers,
    Sansha,
    VigilantTyrannos,
    Edencom,
    Angel,
    Drifters,
    Mixed
}

/// <summary>The e-war kinds this table can name for a type with no SDE dogma at all (see <see cref="TyrannosAgent"/>).
/// Mirrors the nine behaviors <see cref="ISdeAccessor.GetNpcEwarProfile"/> reads from the SDE for a typed NPC.</summary>
public enum NpcEwarKind { Scram, Neut, Web, Damp, TrackingDisrupt, GuidanceDisrupt, Paint, RemoteRepair, Vorton }

/// <summary>One of the two Vigilant Tyrannos ESI agents (Karybdis/Scylla, ids 3019609/3019610) that carry no SDE
/// type at all — the killmail/enemy collector currently drops them silently for want of a type id. Karybdis flies
/// a Drifter battleship with no e-war [confirmed, EVE University wiki, "Abyssal Deadspace", retrieved 2026-09-24: it
/// "does not come with either a Doomsday Weapon or any EWAR abilities"]; "Scylla Tyrannos" is a display name shared
/// by several different Drifter cruiser hulls, at least one of which scrambles [confirmed, Raymond's own gamelog].
/// <see cref="KnownEwar"/> lists only what is actually observed, not a range — no range is measured for either agent.</summary>
public sealed record TyrannosAgent(string Name, AbyssalNpcFaction Faction, IReadOnlySet<NpcEwarKind> KnownEwar);

/// <summary>
/// The abyssal NPC knowledge the SDE does not carry (ET-367): faction per hull word, the two Vigilant Tyrannos
/// agents that have no SDE type, and one tactical note per faction. Everything the SDE DOES carry — e-war, EHP,
/// signature, speed — comes from <see cref="ISdeAccessor.GetNpcEwarProfile"/> instead; this table only fills the
/// gap, and only with facts, never with a guess (<see cref="Faction"/>).
///
/// <para><b>Sources</b> (Raymond, 2026-09-24: SDE first, cheat sheet/web only for what it does not cover, each
/// fact dated and attributed, anything not confirmed in two sources marked [vermoeden]):</para>
/// <list type="bullet">
/// <item>The hull-word table is the ET-342 research (Depot <c>Research/et-342-abyssal-rooms/README.md</c>,
/// 2026-09-24), which resolved 45 of 48 gamelog-observed names to real SDE types and read the surviving hull word
/// off each — confirmed against the live SDE directly (ET-367, build 3542233) for every entry used in this file's
/// own tests.</item>
/// <item>The Convocation of Empyreans' Abyssal Cheat Sheet (updated 2/28/YC124 2022, "All images, concepts, and
/// names are property of CCP Games"), via Raymond's own summary of it (ET-342 comment, 2026-09-24) — facts only,
/// reworded, never its text or images: the Triglavian drone note, the EDENCOM command-ship note, and the Vigilant
/// Tyrannos "close distance fast" note.</item>
/// <item>The EVE University wiki, "Abyssal Deadspace" (<c>wiki.eveuniversity.org/Abyssal_Deadspace</c>, retrieved
/// 2026-09-24) — confirms the Lucid/Devoted/Lucifer/Overmind hull words against the same faction this file already
/// assigns them, and is the source for the Rogue Drones, Sleepers, Angel, EDENCOM-resist and Drifters/Tyrannos
/// notes. Confirmed independently against a second, older source (Massively Overpowered, "The expert Gila's guide
/// to Abyssal Deadspace", 2018) for the Angel resist-weakness and Rogue Drone kiting facts specifically; the rest
/// rests on the University wiki alone and is marked [vermoeden] below where it is not also plain SDE data.</item>
/// </list>
/// </summary>
public static class AbyssalNpcKnowledge
{
    // The adjective a Triglavian/Rogue Drone/etc. abyssal NPC is built on top of — the word that carries the faction,
    // not the hull's role word next to it. ~14-15 entries, measured against the SDE in the ET-342 research; a name
    // outside this list (e.g. a loot structure like the Triglavian Biocombinative Cache) is deliberately unrecognised
    // rather than guessed.
    private static readonly Dictionary<string, AbyssalNpcFaction> FactionByHullWord = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Damavik"] = AbyssalNpcFaction.Triglavian,
        ["Kikimora"] = AbyssalNpcFaction.Triglavian,
        ["Vedmak"] = AbyssalNpcFaction.Triglavian,
        ["Drekavac"] = AbyssalNpcFaction.Triglavian,
        ["Leshak"] = AbyssalNpcFaction.Triglavian,
        ["Rodiva"] = AbyssalNpcFaction.Triglavian,
        ["Tessella"] = AbyssalNpcFaction.RogueDrones,
        ["Tessera"] = AbyssalNpcFaction.RogueDrones,
        ["Overmind"] = AbyssalNpcFaction.RogueDrones,
        ["Lucid"] = AbyssalNpcFaction.Sleepers,
        ["Devoted"] = AbyssalNpcFaction.Sansha,
        ["Ephialtes"] = AbyssalNpcFaction.VigilantTyrannos,
        ["Tyrannos"] = AbyssalNpcFaction.VigilantTyrannos,
        ["Disparu"] = AbyssalNpcFaction.Edencom,
        ["Lucifer"] = AbyssalNpcFaction.Angel,
        ["Drifter"] = AbyssalNpcFaction.Drifters,
    };

    /// <summary>The faction of one enemy name, read off whichever word in it is a known hull word. Null when no word
    /// in the name is recognised — including loot structures like the Triglavian Biocombinative Cache, which is
    /// deliberate: it is not an NPC and must not be allowed to vote on a room's faction.</summary>
    public static AbyssalNpcFaction? FactionOf(string enemyName) =>
        enemyName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => FactionByHullWord.TryGetValue(word, out var faction) ? (AbyssalNpcFaction?)faction : null)
            .FirstOrDefault(faction => faction is not null);

    /// <summary>The faction of a set of enemy names: the one faction they share, or <see cref="AbyssalNpcFaction.Mixed"/>
    /// when two or more distinct factions are recognised among them. Null when none of the names are recognised.
    /// Never a guess — a name this table cannot place (an unrecognised hull word, or a non-NPC name) does not count
    /// either way, so it can neither create nor break a "mixed" reading.</summary>
    public static AbyssalNpcFaction? Faction(IEnumerable<string> names)
    {
        var recognised = names.Select(FactionOf).OfType<AbyssalNpcFaction>().Distinct().ToList();
        return recognised.Count switch
        {
            0 => null,
            1 => recognised[0],
            _ => AbyssalNpcFaction.Mixed,
        };
    }

    private static readonly TyrannosAgent[] TyrannosAgents =
    [
        new("Karybdis Tyrannos", AbyssalNpcFaction.VigilantTyrannos, new HashSet<NpcEwarKind>()),
        new("Scylla Tyrannos", AbyssalNpcFaction.VigilantTyrannos, new HashSet<NpcEwarKind> { NpcEwarKind.Scram }),
    ];

    private static readonly Dictionary<string, TyrannosAgent> TyrannosByName =
        TyrannosAgents.ToDictionary(agent => agent.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The Karybdis or Scylla Tyrannos entry by exact name, or null for any other name. The two Tyrannos
    /// agents are the one case this table names explicitly rather than through <see cref="FactionByHullWord"/>,
    /// since ET-367 AC3 requires both to resolve to a record rather than silently dropping out.</summary>
    public static TyrannosAgent? TyrannosAgentByName(string name) => TyrannosByName.GetValueOrDefault(name);

    /// <summary>One tactical note per faction, reworded — never the source's own wording. See the class-level
    /// <c>Sources</c> list for what backs each one; a note resting on one source only is marked [vermoeden].</summary>
    public static readonly IReadOnlyDictionary<AbyssalNpcFaction, string> FactionNotes = new Dictionary<AbyssalNpcFaction, string>
    {
        // Raymond's own ET-342 comment, 2026-09-24.
        [AbyssalNpcFaction.Triglavian] = "The 'Vila' prefix brings drone support that stops once the ship behind it dies — kill that ship, not the drones.",
        // EVE University wiki, retrieved 2026-09-24 (Overmind's range/damage; frigates' single damage type). [vermoeden]: not cross-checked in a second source.
        [AbyssalNpcFaction.RogueDrones] = "The Overmind battleship out-ranges most fits with Kinetic/Thermal damage; its frigates each carry one damage type and kite easier than they tank.",
        // EVE University wiki, retrieved 2026-09-24 (every Lucid hull carries remote repair, plus webs/neuts). [vermoeden]: not cross-checked in a second source.
        [AbyssalNpcFaction.Sleepers] = "Every Lucid hull carries remote repair, so kill the smallest ones first before the reps land — expect webs and neutralizers too.",
        // EVE University wiki, retrieved 2026-09-24 (the Devoted Knight's resist hole differs from the rest of Sansha's abyssal ships). [vermoeden]: not cross-checked in a second source.
        [AbyssalNpcFaction.Sansha] = "Most hulls are weak to EM and Thermal, but the Devoted Knight itself is weak to EM then Explosive instead — check the hull before picking your damage.",
        // Karybdis/no-e-war: EVE University wiki, retrieved 2026-09-24. Scylla/scramble: Raymond's own gamelog. Both independently confirmed, not [vermoeden].
        [AbyssalNpcFaction.VigilantTyrannos] = "Karybdis Tyrannos has no e-war and just runs at range to drag the fight to the pocket edge; Scylla Tyrannos is a shared name for several cruisers, one of which scrambles.",
        // Command-ship-first: Raymond's own ET-342 comment, 2026-09-24. Thermal/EM weakness: EVE University wiki, retrieved 2026-09-24. [vermoeden]: weakness not cross-checked in a second source.
        [AbyssalNpcFaction.Edencom] = "Weak to Thermal and EM — keep your own speed up and take the command ship down first.",
        // EVE University wiki, retrieved 2026-09-24, cross-checked against Massively Overpowered's 2018 Abyssal guide (both independently state the same EM/Thermal-strong, Explosive/Kinetic-weak resist split).
        [AbyssalNpcFaction.Angel] = "Fast, varied e-war and damage, weak to Explosive and Kinetic — the Cynabal alone combines web, neut and high tracking.",
        // EVE University wiki, retrieved 2026-09-24 (Drifters and Seekers fire omni-damage turrets with flat omni-resists). [vermoeden]: not cross-checked in a second source.
        [AbyssalNpcFaction.Drifters] = "Omni-damage turrets on a flat omni-resist profile — no damage type is favoured either way.",
    };
}
