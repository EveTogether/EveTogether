using Avalonia;
using Avalonia.Controls;

namespace EveUtils.Client.Views.Home;

public partial class HomeDashboardView : UserControl
{
    /// <summary>At and above this the home stands in two columns; below it, one. The docked home at the default
    /// window is ~960 px and gets the single column: the pilots table beside a 420 px column would trim every system
    /// and fit name.</summary>
    public const double WideFrom = 1080;

    public HomeDashboardView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => _ApplyWidth(e.NewSize.Width);
    }

    private void _ApplyWidth(double width)
    {
        bool isNarrow = width < WideFrom;
        Earnings.Classes.Set("narrow", isNarrow);
        Earnings.ColumnDefinitions[3].Width = isNarrow ? new GridLength(0) : new GridLength(1.35, GridUnitType.Star);
    }
}
