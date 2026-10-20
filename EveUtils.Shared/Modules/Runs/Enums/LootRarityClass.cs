namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>What kind of drop an item is in a loot table (ET-519). Everything but <see cref="Common"/> goes to the rare
/// group; <see cref="Seldom"/> is not a catalogue class but an item seen in fewer than 5 % of the runs.</summary>
public enum LootRarityClass
{
    Common,

    /// <summary>A cargo paste does not tell a blueprint original from a copy, so both are just a blueprint.</summary>
    Blueprint,
    Faction,
    Storyline,
    Deadspace,
    Officer,
    Seldom
}
