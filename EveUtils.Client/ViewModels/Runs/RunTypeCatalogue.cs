using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Enums;
using Material.Icons;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One row of the type catalogue: what a run of this type is, and which sections it has. The run window, the detail
/// screen and the runs overview all read it off <see cref="RunTypeCatalogue"/>, so the three can never disagree.
///
/// The facts below the sections are what the screens used to work out from <see cref="ActivityKind"/> on their own,
/// one check at a time (ET-236). They say what the type is; what a section or the clock does with that stays in the
/// section and the clock.
/// </summary>
public sealed record RunTypeDefinition
{
    public required RunTypeId Id { get; init; }

    /// <summary>What TYPE reads as (ET-226).</summary>
    public required string Name { get; init; }

    public required MaterialIconKind Icon { get; init; }

    /// <summary>The run window's title bar.</summary>
    public required string WindowTitle { get; init; }

    /// <summary>How the detail screen names this type when it says which sections it does not have — "a site pays
    /// in what it drops".</summary>
    public required string Noun { get; init; }

    /// <summary>The sections the run window draws for this type. Order is <see cref="RunSectionModules"/>'s.</summary>
    public required IReadOnlyList<RunSectionId> WindowSections { get; init; }

    /// <summary>The sections the detail screen draws for this type even when they are empty. A section missing from
    /// here still appears when the activity has data for it (ET-162).</summary>
    public required IReadOnlyList<RunSectionId> DetailSections { get; init; }

    public RunSpace Space { get; init; } = RunSpace.KnownSpace;

    /// <summary>Handed out by an agent: the agent's level is shown, and the run is flown in the agent's system rather
    /// than wherever the pilot's location says (ET-172 sub 4).</summary>
    public bool HasAgent { get; init; }

    /// <summary>The words this type is looted in. Empty is an answer, not a gap: a mission is not looted in these
    /// words at all, so it gets no list rather than one it half fits (ET-174 AC-4).</summary>
    public IReadOnlyList<RunLootStrategy> LootStrategies { get; init; } = [];

    /// <summary>A type that escalates is one whose detail screen always has an ESCALATION section, and the run window
    /// offers to register one for exactly those (ET-124 measured that only a site escalates). Derived rather than
    /// stated twice, so the two cannot drift apart.</summary>
    public bool Escalates => DetailSections.Contains(RunSectionId.Escalation);
}

/// <summary>
/// The one place a run's TYPE comes from. Adding a new type — ET-228's Homefront kinds, ET-229's Mining detection —
/// is one entry here plus, in <see cref="RunTypeResolver"/> in Shared, one arm resolving something to it, plus the
/// section modules of its own that <see cref="RunSectionModules"/> registers. Nothing else in this app names a run's
/// type or decides its sections on its own.
///
/// Icons are Material.Icons.Avalonia (ET-74), outline variants where one exists and is legible at 14px, chosen to be
/// distinct from each other with no colour at all: a skull, a database, a stone, a gas cylinder, a meteor, a tunnel,
/// a clipboard, a pickaxe, a castle, a storm, a map marker — eleven different silhouettes, not eleven shades of one.
/// </summary>
public static class RunTypeCatalogue
{
    /// <summary>How much of the pocket you opened. Kinds loot in different vocabularies, so each gets its own list
    /// rather than one that half fits each.</summary>
    public static IReadOnlyList<RunLootStrategy> AbyssalLootStrategies { get; } =
        [RunLootStrategy.BioadaptiveOnly, RunLootStrategy.BioadaptiveAndTriglavian, RunLootStrategy.AllCans];

    /// <summary>In order of how much of the site you did, which is why cherry-picked stands second and not last.
    /// Cherry-picking is a site's move and only a site's: an abyssal pocket is instanced for you and your fleet, so
    /// there is nobody to leave the other cans to.</summary>
    public static IReadOnlyList<RunLootStrategy> SiteLootStrategies { get; } =
        [RunLootStrategy.Blitzed, RunLootStrategy.CherryPicked, RunLootStrategy.Cleared, RunLootStrategy.FullClear];

    // Every run window has the same six today, whatever its type. A data or relic site keeps ENEMIES and BOUNTY even
    // though it usually has neither: it can still have rats, and both sections already say what they have not seen
    // (the ET-236 choice, rather than one that appears only once something comes in).
    private static readonly IReadOnlyList<RunSectionId> StandardWindow =
    [
        RunSectionId.Activity, RunSectionId.Enemies, RunSectionId.Fit, RunSectionId.Fleet, RunSectionId.Bounty,
        RunSectionId.Loot
    ];

    private static readonly IReadOnlyList<RunSectionId> SiteDetail =
    [
        RunSectionId.Activity, RunSectionId.Enemies, RunSectionId.Fleet, RunSectionId.Bounty, RunSectionId.Loot,
        RunSectionId.Escalation
    ];

    private static RunTypeDefinition _Site(RunTypeId id, string name, MaterialIconKind icon) => new()
    {
        Id = id,
        Name = name,
        Icon = icon,
        WindowTitle = "SITE RUN",
        Noun = "a site",
        WindowSections = StandardWindow,
        DetailSections = SiteDetail,
        LootStrategies = SiteLootStrategies
    };

    private static readonly IReadOnlyDictionary<RunTypeId, RunTypeDefinition> Definitions = new Dictionary<RunTypeId, RunTypeDefinition>
    {
        // Not "Combat Site": a site whose group could not be resolved reads as what is actually known about it — a
        // site — never the specific kind this ticket exists to stop defaulting to (ET-226 AC-3).
        [RunTypeId.Unknown] = _Site(RunTypeId.Unknown, "Site", MaterialIconKind.MapMarkerOutline),
        [RunTypeId.CombatSite] = _Site(RunTypeId.CombatSite, "Combat Site", MaterialIconKind.SkullOutline),
        [RunTypeId.DataSite] = _Site(RunTypeId.DataSite, "Data Site", MaterialIconKind.DatabaseOutline),
        [RunTypeId.RelicSite] = _Site(RunTypeId.RelicSite, "Relic Site", MaterialIconKind.DiamondStone),
        [RunTypeId.GasSite] = _Site(RunTypeId.GasSite, "Gas Site", MaterialIconKind.GasCylinder),
        [RunTypeId.OreSite] = _Site(RunTypeId.OreSite, "Ore Site", MaterialIconKind.Meteor),
        [RunTypeId.Wormhole] = _Site(RunTypeId.Wormhole, "Wormhole", MaterialIconKind.TunnelOutline),
        [RunTypeId.Mission] = new()
        {
            Id = RunTypeId.Mission,
            Name = "Mission run",
            Icon = MaterialIconKind.ClipboardTextOutline,
            WindowTitle = "MISSION RUN",
            Noun = "a mission",
            // Diverges from StandardWindow: MISSION stands right after ACTIVITY, where a mission's own facts belong
            // (ET-237) — nothing else names the same run window sections in a different order (RunSectionModules
            // fixes the order every type is checked against).
            WindowSections =
            [
                RunSectionId.Activity, RunSectionId.Mission, RunSectionId.Enemies, RunSectionId.Fit,
                RunSectionId.Fleet, RunSectionId.Bounty, RunSectionId.Loot
            ],
            DetailSections = [RunSectionId.Activity, RunSectionId.Mission, RunSectionId.Enemies, RunSectionId.Fleet],
            HasAgent = true,
            // Same four a site loots by, same order (ET-172 backlog gap, closed by ET-237): a courier has nothing to
            // blitz or clear, so the row stays optional with no preselection — IsLootStrategyShown already hides an
            // empty list, and nothing here forces a choice on one that has none to make.
            LootStrategies = SiteLootStrategies
        },
        // Nothing resolves to Mining yet (ET-229 adds its detection and its MINING section); until then it carries
        // only what every run has.
        [RunTypeId.Mining] = new()
        {
            Id = RunTypeId.Mining,
            Name = "Mining",
            Icon = MaterialIconKind.Pickaxe,
            WindowTitle = "MINING RUN",
            Noun = "a mining run",
            WindowSections = [RunSectionId.Activity, RunSectionId.Fleet],
            DetailSections = [RunSectionId.Activity, RunSectionId.Fleet]
        },
        // Nothing resolves to Homefront yet (ET-228). A homefront is flown as a site today and reads as one, so it
        // keeps a site's sections until ET-230 gives it its own.
        [RunTypeId.Homefront] = _Site(RunTypeId.Homefront, "Homefront", MaterialIconKind.Castle),
        [RunTypeId.Abyssal] = new()
        {
            Id = RunTypeId.Abyssal,
            Name = "Abyssal",
            Icon = MaterialIconKind.StormOutline,
            WindowTitle = "ABYSSAL RUN",
            Noun = "an abyssal pocket",
            WindowSections = StandardWindow,
            DetailSections =
                [RunSectionId.Activity, RunSectionId.Enemies, RunSectionId.Fleet, RunSectionId.Bounty, RunSectionId.Loot],
            Space = RunSpace.AbyssalPocket,
            LootStrategies = AbyssalLootStrategies
        }
    };

    /// <summary>A run stored by a later build under an <see cref="ActivityKind"/> this one has never heard of. It still
    /// opens and still reads as a run — the window going down over a header is the failure AGENTS.md §2 forbids — and
    /// it claims nothing it cannot know about: TYPE reads like any unresolved site (ET-226), and the detail screen only
    /// draws what every run has.</summary>
    public static RunTypeDefinition NewerBuildKind { get; } = new()
    {
        Id = RunTypeId.Unknown,
        Name = "Site",
        Icon = MaterialIconKind.MapMarkerOutline,
        WindowTitle = "RUN",
        Noun = "this kind of activity",
        WindowSections = StandardWindow,
        DetailSections = [RunSectionId.Activity, RunSectionId.Enemies, RunSectionId.Fleet]
    };

    /// <summary>Every row, the newer-build one included — what the catalogue's own tests walk.</summary>
    public static IReadOnlyList<RunTypeDefinition> All => [.. Definitions.Values, NewerBuildKind];

    /// <summary>The key each row is filed under, for the test proving no row sits under another type's key.</summary>
    public static IEnumerable<KeyValuePair<RunTypeId, RunTypeDefinition>> Rows => Definitions;

    // Defensive rather than reachable today: RunTypeResolver only ever returns a member this dictionary carries.
    // The guard is for the one way that could stop being true — a RunTypeId appended without its catalogue row
    // following in the same change — where falling back to Unknown's own row still reads as a site, not a crash
    // (AGENTS.md §2).
    public static RunTypeDefinition For(RunTypeId id) =>
        Definitions.TryGetValue(id, out RunTypeDefinition? definition) ? definition : Definitions[RunTypeId.Unknown];

    /// <summary>The type of a run as it is carried — its kind and the scanner group it was copied with — which is how
    /// both run screens ask.</summary>
    public static RunTypeDefinition For(ActivityKind kind, string? signatureGroup) =>
        Enum.IsDefined(kind) ? For(RunTypeResolver.Resolve(kind, signatureGroup)) : NewerBuildKind;
}
