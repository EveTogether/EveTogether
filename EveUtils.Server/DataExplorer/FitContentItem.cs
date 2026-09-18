namespace EveUtils.Server.DataExplorer;

/// <summary>One item type in one slot group, with the quantities of every slot it fills added up.</summary>
public sealed class FitContentItem
{
    public required FitSlot Slot { get; init; }
    public required int TypeId { get; init; }
    public required int Quantity { get; init; }
}
