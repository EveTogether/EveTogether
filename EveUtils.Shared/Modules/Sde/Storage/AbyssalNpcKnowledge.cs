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

/// <summary>A Tyrannos ESI agent without an SDE type. <see cref="KnownEwar"/> contains only observed
/// effects; no ranges are known. See ET-342 research for sources.</summary>
public sealed record TyrannosAgent(string Name, AbyssalNpcFaction Faction, IReadOnlySet<NpcEwarKind> KnownEwar);

/// <summary>Faction, Tyrannos agents and tactical notes absent from the SDE. E-war and defenses come from
/// <see cref="ISdeAccessor.GetNpcEwarProfile"/>. Sources: ET-342 research in Depot and the ET-367 PR.</summary>
public static class AbyssalNpcKnowledge
{
    // Match faction-bearing hull words only; unknown names and loot structures stay unrecognised.
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

    /// <summary>The shared faction, <see cref="AbyssalNpcFaction.Mixed"/> for multiple known factions,
    /// or null when no name is recognised. Unknown names do not affect the result.</summary>
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
