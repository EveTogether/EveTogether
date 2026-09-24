using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Updates;

namespace EveUtils.Client.Views;

/// <summary>
/// The update offer: the installed and offered build labels, the download size and the notes the feed carries. Returns true if the
/// user pressed "Download and install", false on "Later" or a plain close — nothing is fetched until they say so.
/// </summary>
public partial class UpdateAvailableWindow : ChromedWindow
{
    public UpdateAvailableWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public UpdateAvailableWindow(string installedVersion, AppRelease release) : this()
    {
        if (this.FindControl<TextBlock>("InstalledBlock") is { } installedBlock)
        {
            installedBlock.Text = installedVersion;
        }
        if (this.FindControl<TextBlock>("AvailableBlock") is { } availableBlock)
        {
            availableBlock.Text = release.DisplayVersion;
        }
        if (this.FindControl<SelectableTextBlock>("NotesBlock") is { } notesBlock)
        {
            notesBlock.Text = string.IsNullOrWhiteSpace(release.Notes)
                ? "This release ships without notes."
                : release.Notes.Trim();
        }

        // A feed that reports no size would otherwise show "0 MB", which reads as a fact rather than a gap.
        if (this.FindControl<StackPanel>("DownloadSizePanel") is { } downloadSizePanel)
        {
            downloadSizePanel.IsVisible = release.SizeBytes > 0;
        }
        if (this.FindControl<TextBlock>("SizeBlock") is { } sizeBlock)
        {
            sizeBlock.Text = UpdateDownloadSize.Format(release.SizeBytes);
        }
    }

    private void OnLater(object? sender, RoutedEventArgs e) => Close(false);

    private void OnDownload(object? sender, RoutedEventArgs e) => Close(true);
}
