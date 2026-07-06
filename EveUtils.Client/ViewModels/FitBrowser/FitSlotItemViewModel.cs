namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One item in a fit's slot list. The display name is resolved from the SDE (falling back to
/// <c>type {id}</c>); the raw <see cref="Flag"/> keeps the exact slot position so gaps stay visible.</summary>
public sealed class FitSlotItemViewModel
{
    public string Flag { get; }
    public int TypeId { get; }
    public int Quantity { get; }

    /// <summary>Resolved type name (or <c>type {id}</c> until the SDE is imported).</summary>
    public string TypeLabel { get; }

    /// <summary>Shown only when more than one of the same item sits in the slot/bay (drones, charges, cargo).</summary>
    public string QuantityLabel => Quantity > 1 ? $"×{Quantity}" : "";

    public FitSlotItemViewModel(string flag, int typeId, int quantity, string typeLabel)
    {
        Flag = flag;
        TypeId = typeId;
        Quantity = quantity;
        TypeLabel = typeLabel;
    }
}
