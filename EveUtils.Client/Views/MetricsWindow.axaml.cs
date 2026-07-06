using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// Per-character metrics window. Non-modal so its live graphs/stats keep updating beside the main
/// window; disposes its view-model (stops the refresh timer) on close.
/// </summary>
public partial class MetricsWindow : ChromedWindow
{
    public MetricsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public MetricsWindow(MetricsWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }
}
