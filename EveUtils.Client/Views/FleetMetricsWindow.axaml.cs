using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// Free-standing fleet-metrics window: one live DPS graph per active member plus the fleet roll-up
/// totals. Non-modal so its graphs keep updating beside the main + fleets windows; disposes its view-model (drops
/// the bus subscription) on close.
/// </summary>
public partial class FleetMetricsWindow : ChromedWindow
{
    public FleetMetricsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FleetMetricsWindow(FleetMetricsViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }
}
