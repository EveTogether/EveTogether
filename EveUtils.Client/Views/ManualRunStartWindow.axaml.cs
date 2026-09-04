using System;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.Views;

/// <summary>The manual run-start screen (ET-163), hosted like the other feature modules: a docked tab when docked,
/// its own window when floating.</summary>
public partial class ManualRunStartWindow : ChromedWindow, IHostableModuleWindow
{
    public Action? CloseRequested { get; set; }

    public ManualRunStartWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ManualRunStartWindow(ManualRunStartViewModel viewModel) : this() => DataContext = viewModel;

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (CloseRequested is not null) CloseRequested();
        else Close();
    }
}
