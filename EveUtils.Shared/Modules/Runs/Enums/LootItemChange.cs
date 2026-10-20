namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Whether an item's drop changed around the last game update (ET-519), told from the runs before and after it
/// when there are enough on both sides.</summary>
public enum LootItemChange
{
    None,
    NotSeenSinceUpdate,
    NewSinceUpdate
}
