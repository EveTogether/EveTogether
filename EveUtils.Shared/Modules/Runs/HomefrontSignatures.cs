namespace EveUtils.Shared.Modules.Runs;

/// <summary>
/// The NPCs and objects that name one homefront site on their own (ET-348). The SDE carries no dungeon spawn data,
/// so this table is curated by hand from each site's <c>gameplayDescription</c>, which names its hallmark objects.
/// It is keyed on type id, never on a name, so it works whatever language the gamelog is written in (ET-278).
/// A guard test checks every row against the installed SDE, so a content change by CCP fails loudly.
///
/// Ordinary homefront enemy ships (groups 4569-4574) are left out on purpose: the same factions fly in several
/// sites, so they never say which one it is.
/// </summary>
public static class HomefrontSignatures
{
    public static IReadOnlyDictionary<int, int> DungeonIdByTypeId { get; } = new Dictionary<int, int>
    {
        // Dread Assault: the friendly dreadnought you cap, and the structure it shoots.
        [77019] = 10320, [77065] = 10320,   // Legatus Bane, Blood Raider Temple
        [77184] = 10381, [77251] = 10381,   // Arbiter Karura, Occupied Industrial Housing
        [77185] = 10383, [77252] = 10383,   // Peacekeeper Hubris, Decommissioned Base
        [77183] = 10384, [77253] = 10384,   // Verndari Valravn, Cartel Racket HQ

        // Raid: the fleeing hauler.
        [77066] = 10347,                    // Offertory Sigil
        [77505] = 10377,                    // Badger Runner
        [77506] = 10378,                    // Nereus Mule
        [77507] = 10379,                    // Plunder Wreathe

        // Emergency Aid: the friendly object you keep alive.
        [77126] = 10238,                    // Holy Mission
        [77287] = 10373,                    // Charon Repatriator
        [77290] = 10376,                    // Master Controller
        [77292] = 10380,                    // Clan Commons

        // Suspicious Signal: the structure you destroy, and the arrays you hack (group 306, Spawn Container).
        [77120] = 10309, [77121] = 10309,   // Longshot Relay, Remote Armor Array
        [77195] = 10367, [77196] = 10367,   // Quantum-Override Broadcaster, Remote Jamming Array
        [77199] = 10368, [77198] = 10368,   // Sacred Exploiter, Remote Cloaking Array
        [77200] = 10369, [77197] = 10369,   // Megafinance Siphoner, Remote Shield Array

        // Stabilize Rift: the arrays you neut.
        [81402] = 10714                     // Destabilizing Array
    };
}
