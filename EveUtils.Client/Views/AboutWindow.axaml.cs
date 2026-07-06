using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>The About dialog: app identity + version, creator credits with portraits, inspiration links, the AGPLv3
/// license and the mandatory CCP attribution. A modal, fixed-size chromed window — purely informational.</summary>
public partial class AboutWindow : ChromedWindow
{
    public AboutWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public AboutWindow(AboutViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Opens a credit/reference link in the system browser; the URL travels on the control's Tag so the view model
    // stays free of platform launch concerns (mirrors TypeInfoWindow).
    private void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string url } && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            _ = launcher.LaunchUriAsync(new Uri(url));
    }
}
