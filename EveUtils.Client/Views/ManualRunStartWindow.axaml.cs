using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.Views;

/// <summary>The manual run-start dialog (ET-163): a modal filling-in moment, closed by its own view model the
/// moment the run exists — from there the run lives in the activity window, like every other run.</summary>
public partial class ManualRunStartWindow : ChromedWindow
{
    public ManualRunStartWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ManualRunStartWindow(ManualRunStartViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += Close;   // the run is started; the dialog has nothing left to show
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
