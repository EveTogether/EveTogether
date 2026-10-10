using System.Globalization;

namespace EveUtils.Client.ViewModels;

/// <summary>One material of the shopping list, summed over the checked blueprints (ET-502).</summary>
public sealed class ShoppingListLineViewModel(int typeId, string name, long quantity)
{
    public TypeIconViewModel Icon { get; } = new(typeId);

    public string Name { get; } = name;

    public long Quantity { get; } = quantity;

    public string QuantityText => Quantity.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The line as EVE's multibuy reads it: the name, a space, the amount without grouping.</summary>
    public string MultibuyLine => $"{Name} {Quantity.ToString(CultureInfo.InvariantCulture)}";
}
