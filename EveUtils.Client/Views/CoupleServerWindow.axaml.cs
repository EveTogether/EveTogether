using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.Coupling;

namespace EveUtils.Client.Views;

/// <summary>
/// "Couple to server" from a character's settings (ET-428): the same coupling as the setup wizard's server step, so
/// connecting, the Cancel that stops it and the errors you retry all happen here rather than in the main window's
/// status bar. Closing the window stops a coupling still under way.
/// </summary>
public partial class CoupleServerWindow : ChromedWindow
{
    public CoupleServerWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public CoupleServerWindow(ServerCoupleViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.NotNowRequested += Close;
        Closed += (_, _) =>
        {
            viewModel.NotNowRequested -= Close;
            viewModel.CancelPairing();
        };
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
