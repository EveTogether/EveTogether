using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>The message inbox window, opened non-modally from the main window's ✉ button so deliveries
/// keep landing while it stays open. Its <see cref="InboxViewModel"/> is shared with the main window (it owns
/// the live collection + unread badge), so it is not disposed on close.</summary>
public partial class InboxWindow : ChromedWindow, IHostableModuleWindow
{
    public Action? CloseRequested { get; set; }

    public InboxWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public InboxWindow(InboxViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (CloseRequested is not null) CloseRequested();
        else Close();
    }
}
