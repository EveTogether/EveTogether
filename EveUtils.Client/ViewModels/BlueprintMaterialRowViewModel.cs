using System.Globalization;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Market.Services;

namespace EveUtils.Client.ViewModels;

/// <summary>One material of the selected blueprint's breakdown.</summary>
public sealed class BlueprintMaterialRowViewModel(string name, BlueprintMaterialCost material)
{
    public string Name { get; } = name;

    public string NeededText { get; } = material.Needed.ToString("N0", CultureInfo.InvariantCulture);

    public string UnitPriceText { get; } = material.UnitPrice is { } price ? IskFormat.Exact((double)price) : "no price";

    public string TotalText { get; } = IskFormat.NumberOrNoPrice(material.Total);

    public bool HasPrice { get; } = material.UnitPrice is not null;
}
