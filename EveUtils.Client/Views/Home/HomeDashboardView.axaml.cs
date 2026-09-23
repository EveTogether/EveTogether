using Avalonia;
using Avalonia.Controls;

namespace EveUtils.Client.Views.Home;

public partial class HomeDashboardView : UserControl
{
    /// <summary>At and above this the home stands in two columns; below it, one. The docked home at the default
    /// window is ~960 px and gets the single column: the pilots table beside a 420 px column would trim every system
    /// and fit name.</summary>
    public const double WideFrom = 1080;

    /// <summary>Fleets, fits and activity beside the left column: the runs overview's pane width plus its rule.</summary>
    private const double RightColumnWidth = 420;

    /// <summary>The 30-day chart beside the three tiles, a little wider than one of them.</summary>
    private const double ChartShare = 1.35;

    public HomeDashboardView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => _ApplyWidth(e.NewSize.Width);
    }

    private void _ApplyWidth(double width)
    {
        bool isNarrow = width < WideFrom;
        Earnings.Classes.Set("narrow", isNarrow);
        Earnings.ColumnDefinitions[3].Width = isNarrow ? new GridLength(0) : new GridLength(ChartShare, GridUnitType.Star);

        Body.Classes.Set("narrow", isNarrow);
        Body.ColumnDefinitions[1].Width = isNarrow ? new GridLength(0) : new GridLength(RightColumnWidth);
        Grid.SetColumn(RightColumn, isNarrow ? 0 : 1);
        Grid.SetRow(RightColumn, isNarrow ? 1 : 0);
        RightColumn.BorderThickness = isNarrow ? new Thickness(0) : new Thickness(1, 0, 0, 0);
    }
}
