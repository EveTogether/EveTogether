using Avalonia.Media.Imaging;
using EveUtils.Client.Formatting;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>A "Show Info" card for a module or charge: an Info tab (name, group · category, estimated market
/// value and icon) plus a Links tab with external deep-links (everef.net, EVE Market Browser, EVE Workbench) built from
/// the type id. Built by the caller from the SDE, the price cache and the image provider.</summary>
public sealed class TypeInfoWindowViewModel
{
    public string Name { get; }
    public string Category { get; }
    public string Price { get; }
    public Bitmap? Image { get; }
    public bool HasImage => Image is not null;

    /// <summary>everef.net item reference for this type.</summary>
    public string EverefUrl { get; }
    /// <summary>EVE Market Browser prices for this type (region 0 = the default/all-regions view).</summary>
    public string MarketBrowserUrl { get; }
    /// <summary>EVE Workbench sell-market listing for this type.</summary>
    public string WorkbenchUrl { get; }

    public TypeInfoWindowViewModel(int typeId, string name, string category, double? averagePrice, Bitmap? image)
    {
        Name = name;
        Category = category;
        Price = averagePrice is > 0 ? IskFormat.Short(averagePrice.Value) : "—";
        Image = image;
        EverefUrl = $"https://everef.net/types/{typeId}";
        MarketBrowserUrl = $"https://evemarketbrowser.com/region/0/type/{typeId}";
        WorkbenchUrl = $"https://eveworkbench.com/market/sell/{typeId}";
    }
}
