using System.Globalization;
using EveUtils.Client.Formatting;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>One of the top items in the compact run window's loot hover.</summary>
public sealed class CompactLootLineViewModel(string name, long quantity, decimal? value, bool isBlueprintAppraisal = false)
{
    public string Name { get; } = name;

    public string QuantityText { get; } = $"×{quantity.ToString("N0", CultureInfo.CurrentCulture)}";

    public string ValueText { get; } = IskFormat.WholeOrNoPrice(value);

    /// <summary>Valued by a blueprint build appraisal (ET-501), said in a badge beside the name (ET-503).</summary>
    public bool IsBlueprintAppraisal { get; } = isBlueprintAppraisal;
}
