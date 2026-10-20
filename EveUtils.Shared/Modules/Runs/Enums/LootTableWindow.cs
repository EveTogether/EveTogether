namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>The span a loot table is read over (ET-519, Jithran 18-10): the runs do not store the game version, so
/// "since the last update" is the SDE's release date, never further back than the 90 days.</summary>
public enum LootTableWindow
{
    Days30,
    Days90,
    SinceUpdate
}
