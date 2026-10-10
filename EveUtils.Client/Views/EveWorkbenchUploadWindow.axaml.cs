using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.Views;

/// <summary>The upload dialog of one activity (ET-325); closes itself once the upload was handed over.</summary>
public partial class EveWorkbenchUploadWindow : ChromedWindow
{
    public EveWorkbenchUploadWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public EveWorkbenchUploadWindow(EveWorkbenchUploadViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
        Closed += (_, _) => viewModel.CloseRequested -= Close;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
