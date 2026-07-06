using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// The Fleets window: MY FLEETS + DISCOVER on top, the active-fleet member graphs below. Opened non-modal
/// from the main window so its live graphs keep updating. Title is static (set in XAML) — no ElementName bug here.
/// </summary>
public partial class FleetsWindow : ChromedWindow
{
    public FleetsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public FleetsWindow(FleetsViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose(); // release the fleet.changed subscription when the window closes
    }
}
