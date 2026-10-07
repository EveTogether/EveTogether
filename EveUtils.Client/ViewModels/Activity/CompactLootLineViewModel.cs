using System.Globalization;
using EveUtils.Client.Formatting;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>One of the top items in the compact run window's loot hover.</summary>
public sealed class CompactLootLineViewModel(string name, long quantity, decimal? value)
{
    public string Name { get; } = name;

    public string QuantityText { get; } = $"×{quantity.ToString("N0", CultureInfo.CurrentCulture)}";

    public string ValueText { get; } = IskFormat.WholeOrNoPrice(value);
}
