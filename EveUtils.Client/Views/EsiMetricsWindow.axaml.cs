using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>The client ESI-metrics window, opened non-modally from the main window. Its
/// <see cref="EsiMetricsViewModel"/> is created per open and disposed on close so its live poll timer only runs while
/// the window is visible.</summary>
public partial class EsiMetricsWindow : ChromedWindow, IHostableModuleWindow
{
    public Action? CloseRequested { get; set; }

    public EsiMetricsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public EsiMetricsWindow(EsiMetricsViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (CloseRequested is not null) CloseRequested();
        else Close();
    }
}
