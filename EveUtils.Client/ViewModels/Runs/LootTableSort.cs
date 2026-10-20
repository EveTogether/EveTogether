namespace EveUtils.Client.ViewModels.Runs;

/// <summary>The columns the loot table sorts on (ET-519); the default is the item's contribution to the loot value.</summary>
public enum LootTableSort
{
    Share,
    Name,
    Rate,
    Quantity,
    Value
}
