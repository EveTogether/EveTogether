namespace EveUtils.Client.ViewModels.Activity;

/// <summary>Which compact version of the run window opens (ET-478). Stored by name, so a member can be added without
/// renumbering what a pilot already chose.</summary>
public enum CompactRunStyle
{
    /// <summary>A small card: clock, total, loot, bounty and the run buttons.</summary>
    Card,

    /// <summary>One line, the details behind hover.</summary>
    Hud
}
