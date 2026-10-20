using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EveUtils.Client.Views;

/// <summary>The figures of an activity's expected earnings in one compact block (ET-524), shared by the run window, the run
/// details and the runs drawer: the typical run and the band across the top, the bar with this run's label on it, and the
/// worst and best run at the ends of the bar.</summary>
public partial class EarningsRangeStrip : UserControl
{
    public EarningsRangeStrip() => AvaloniaXamlLoader.Load(this);
}
