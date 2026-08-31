using System.Globalization;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Market.Services;

namespace EveUtils.Client.ViewModels;

/// <summary>One valued line as the grid shows it: the figures for sorting, the strings for reading.</summary>
public sealed class AppraisalRowViewModel(AppraisalRow row)
{
    public string Name => row.Line.Name;

    public long Quantity => row.Line.Quantity;

    /// <summary>Zero when the source has no price for this type, which the readout spells as "—" rather than 0 ISK.</summary>
    public double UnitPrice => row.Price?.Estimate ?? 0;

    public double Total => UnitPrice * Quantity;

    public string QuantityDisplay => Quantity.ToString("N0", CultureInfo.InvariantCulture);

    public string UnitPriceDisplay => IskFormat.Short(UnitPrice);

    public string TotalDisplay => IskFormat.Short(Total);
}
