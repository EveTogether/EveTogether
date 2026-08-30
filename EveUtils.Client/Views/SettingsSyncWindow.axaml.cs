using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// The EVE Settings Sync tool window (first entry under Tools). Hosted like the other feature modules: a docked tab
/// when docked, its own window when floating. Only the folder picker lives here — it needs the window's
/// <c>StorageProvider</c>, which is exactly the sort of thing the view-model stays free of.
/// </summary>
public partial class SettingsSyncWindow : ChromedWindow, IHostableModuleWindow
{
    public Action? CloseRequested { get; set; }

    /// <summary>Below this content width the backup column has to give way: the docked host inside a 1100-wide
    /// main window is a good 400px narrower than this tool's own window, and a fixed column eats the room the two
    /// sync blocks need to stay readable.</summary>
    private const double WideLayoutWidth = 940;

    /// <summary>Below this body height the standing explanations cost more than they are worth: the two file lists
    /// are down to a single row, which is the one thing this screen cannot afford to lose.</summary>
    private const double CompactLayoutHeight = 430;

    private Grid? _bodyGrid;
    private Panel? _rootPanel;
    private bool? _isWide;

    public SettingsSyncWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _bodyGrid = this.FindControl<Grid>("SyncBodyGrid");
        _rootPanel = this.FindControl<DockPanel>("SyncRootPanel");
        if (_bodyGrid is not null)
            _bodyGrid.SizeChanged += (_, e) => _ApplyViewport(e.NewSize);
    }

    public SettingsSyncWindow(SettingsSyncViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();   // release the settings-watch subscription with the window
    }

    // Two shells, two sizes: the tool's own window has room for a full backup column and the standing
    // explanations, the docked tab has neither and would rather spend that room on the file lists.
    private void _ApplyViewport(Size size)
    {
        if (_bodyGrid is null)
            return;

        var wide = size.Width >= WideLayoutWidth;
        if (wide != _isWide)
        {
            _isWide = wide;
            _bodyGrid.ColumnDefinitions = new ColumnDefinitions(wide ? "*,12,340" : "*,10,250");
        }

        _rootPanel?.Classes.Set("compact", size.Height < CompactLayoutHeight);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsSyncViewModel viewModel)
            return;

        // From the button, not from this window: docked, the module host has moved this content into the main
        // window and this Window is never shown, so its own StorageProvider has no top level behind it.
        var storage = (sender is Visual visual ? TopLevel.GetTopLevel(visual) : this)?.StorageProvider;
        if (storage is null)
            return;

        var options = new FolderPickerOpenOptions
        {
            Title = "Select the EVE install folder that holds the settings_* directories",
            AllowMultiple = false
        };
        if (!string.IsNullOrWhiteSpace(viewModel.InstallRoot))
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(viewModel.InstallRoot);

        var picked = await storage.OpenFolderPickerAsync(options);
        if (picked.Count == 0)
            return;

        await viewModel.PickInstallRootAsync(picked[0].Path.LocalPath);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (CloseRequested is not null) CloseRequested();
        else Close();
    }
}
