using System.Collections.Generic;
using EveUtils.Shared.Modules.Runs.Enums;
using Material.Icons;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One row of the type catalogue (ET-226): what TYPE reads as, and its icon — the same pair the run window,
/// the detail screen and the runs overview all read off <see cref="RunTypeCatalogue.For"/>, so the three can never
/// disagree. <see cref="Sections"/> is the seam ET-236 (the section framework) reads once it exists; every entry here
/// leaves it empty rather than guess at a shape that ticket has not decided yet (AGENTS.md §1, "reserve ≠ build").
/// </summary>
public sealed record RunTypeDefinition(RunTypeId Id, string Name, MaterialIconKind Icon, IReadOnlyList<string> Sections);

/// <summary>
/// The one place a run's TYPE comes from. Adding a new type — ET-228's Homefront kinds, ET-229's Mining detection —
/// is one entry here plus, in <see cref="RunTypeResolver"/> in Shared, one arm resolving something to it; nothing
/// else in this app names a run's type on its own.
///
/// Icons are Material.Icons.Avalonia (ET-74), outline variants where one exists and is legible at 14px, chosen to be
/// distinct from each other with no colour at all: a skull, a database, a stone, a gas cylinder, a meteor, a tunnel,
/// a clipboard, a pickaxe, a castle, a storm, a map marker — eleven different silhouettes, not eleven shades of one.
/// </summary>
public static class RunTypeCatalogue
{
    private static readonly IReadOnlyDictionary<RunTypeId, RunTypeDefinition> Definitions = new Dictionary<RunTypeId, RunTypeDefinition>
    {
        // Not "Combat Site": a site whose group could not be resolved reads as what is actually known about it — a
        // site — never the specific kind this ticket exists to stop defaulting to (ET-226 AC-3).
        [RunTypeId.Unknown] = new(RunTypeId.Unknown, "Site", MaterialIconKind.MapMarkerOutline, []),
        [RunTypeId.CombatSite] = new(RunTypeId.CombatSite, "Combat Site", MaterialIconKind.SkullOutline, []),
        [RunTypeId.DataSite] = new(RunTypeId.DataSite, "Data Site", MaterialIconKind.DatabaseOutline, []),
        [RunTypeId.RelicSite] = new(RunTypeId.RelicSite, "Relic Site", MaterialIconKind.DiamondStone, []),
        [RunTypeId.GasSite] = new(RunTypeId.GasSite, "Gas Site", MaterialIconKind.GasCylinder, []),
        [RunTypeId.OreSite] = new(RunTypeId.OreSite, "Ore Site", MaterialIconKind.Meteor, []),
        [RunTypeId.Wormhole] = new(RunTypeId.Wormhole, "Wormhole", MaterialIconKind.TunnelOutline, []),
        [RunTypeId.Mission] = new(RunTypeId.Mission, "Mission run", MaterialIconKind.ClipboardTextOutline, []),
        [RunTypeId.Mining] = new(RunTypeId.Mining, "Mining", MaterialIconKind.Pickaxe, []),
        [RunTypeId.Homefront] = new(RunTypeId.Homefront, "Homefront", MaterialIconKind.Castle, []),
        [RunTypeId.Abyssal] = new(RunTypeId.Abyssal, "Abyssal", MaterialIconKind.StormOutline, [])
    };

    // Defensive rather than reachable today: RunTypeResolver only ever returns a member this dictionary carries.
    // The guard is for the one way that could stop being true — a RunTypeId appended without its catalogue row
    // following in the same change — where falling back to Unknown's own row still reads as a site, not a crash
    // (AGENTS.md §2).
    public static RunTypeDefinition For(RunTypeId id) =>
        Definitions.TryGetValue(id, out RunTypeDefinition? definition) ? definition : Definitions[RunTypeId.Unknown];
}
