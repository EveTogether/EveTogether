using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.ViewModels.Widgets;

namespace EveUtils.Client.Views.Widgets;

/// <summary>
/// The WIDGET MANAGER screen (ET-434), hosted like GAME LOGS. The clipboard and the browser are reached from here: the
/// view-model only knows the URL. Hosted as a docked tab the content lives in the main window, so the top level is
/// looked up from the clicked control, not from this window.
/// </summary>
public partial class WidgetManagerWindow : ChromedWindow
{
    private static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.5);

    public WidgetManagerWindow() => AvaloniaXamlLoader.Load(this);

    public WidgetManagerWindow(WidgetManagerViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    private void OnCopyUrl(object? sender, RoutedEventArgs e) => _ = _CopyAsync(sender, _UrlOf(sender));

    private void OnCopyApiKey(object? sender, RoutedEventArgs e) =>
        _ = _CopyAsync(sender, (DataContext as WidgetManagerViewModel)?.ApiKey);

    private void OnOpenUrl(object? sender, RoutedEventArgs e)
    {
        if (_UrlOf(sender) is { } url && sender is Visual visual)
            _ = TopLevel.GetTopLevel(visual)?.Launcher.LaunchUriAsync(new Uri(url));
    }

    private static string? _UrlOf(object? sender) => (sender as Control)?.DataContext switch
    {
        WidgetTileViewModel tile => tile.Url,
        WidgetEditorViewModel editor => editor.Url,
        _ => null
    };

    // The button says "Copied" for a moment: a copy has no other visible result.
    private static async Task _CopyAsync(object? sender, string? text)
    {
        if (text is null || sender is not Button button || TopLevel.GetTopLevel(button)?.Clipboard is not { } clipboard)
            return;

        await clipboard.SetTextAsync(text);
        var label = button.Content;
        button.Content = "Copied";
        await Task.Delay(CopiedFor);
        button.Content = label;
    }
}
