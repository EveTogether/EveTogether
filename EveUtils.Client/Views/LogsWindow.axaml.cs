using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>The client log window, opened non-modally from the main window so new entries keep
/// arriving while it stays open. Its <see cref="ClientLogViewModel"/> is shared with the main window, so it is
/// not disposed on close.</summary>
public partial class LogsWindow : ChromedWindow, IHostableModuleWindow
{
    public Action? CloseRequested { get; set; }

    public LogsWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public LogsWindow(ClientLogViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (CloseRequested is not null) CloseRequested();
        else Close();
    }
}
