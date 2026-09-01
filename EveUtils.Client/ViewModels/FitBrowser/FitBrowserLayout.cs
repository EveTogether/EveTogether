namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// How the fit browser lays its fits out — the one input the view switches its item template and panel on, the same
/// shape as <see cref="EveUtils.Client.Fleet.FleetMetricsLayout"/>. The two densities answer different questions:
/// cards to recognise a fit, the table to compare fits against each other.
/// </summary>
public enum FitBrowserLayout
{
    /// <summary>A grid of cards: hull render, fit name over it, the three headline figures and the price. The
    /// default — a fit is recognised by its hull long before it is read by its name.</summary>
    Cards = 0,

    /// <summary>The sortable table: every column side by side, for comparing fits rather than recognising one.</summary>
    List = 1
}
