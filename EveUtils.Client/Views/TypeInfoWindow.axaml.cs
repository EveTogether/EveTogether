using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.FitBrowser;

namespace EveUtils.Client.Views;

/// <summary>A "Show Info" card for a type, opened from a module box's right-click menu: an Info tab plus a
/// Links tab with external deep-links.</summary>
public partial class TypeInfoWindow : ChromedWindow
{
    public TypeInfoWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public TypeInfoWindow(TypeInfoWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Opens an external reference link (everef.net / EVE Market Browser / EVE Workbench) in the system browser; the URL
    // travels on the button's Tag so the view model stays free of platform launch concerns.
    private void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string url } && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            _ = launcher.LaunchUriAsync(new Uri(url));
    }
}
