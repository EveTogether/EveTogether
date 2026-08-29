using System;
using System.Threading.Tasks;
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

    private Grid? _bodyGrid;
    private bool? _isWide;

    public SettingsSyncWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _bodyGrid = this.FindControl<Grid>("SyncBodyGrid");
        if (_bodyGrid is not null)
            _bodyGrid.SizeChanged += (_, e) => _ApplyWidth(e.NewSize.Width);
    }

    public SettingsSyncWindow(SettingsSyncViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    // Two shells, two widths: the tool's own window has room for a full backup column, the docked tab does not.
    private void _ApplyWidth(double width)
    {
        if (_bodyGrid is null)
            return;

        var wide = width >= WideLayoutWidth;
        if (wide == _isWide)
            return;

        _isWide = wide;
        _bodyGrid.ColumnDefinitions = new ColumnDefinitions(wide ? "*,12,340" : "*,10,250");
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsSyncViewModel viewModel)
            return;

        var options = new FolderPickerOpenOptions
        {
            Title = "Select the EVE install folder that holds the settings_* directories",
            AllowMultiple = false
        };
        if (!string.IsNullOrWhiteSpace(viewModel.InstallRoot))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(viewModel.InstallRoot);

        var picked = await StorageProvider.OpenFolderPickerAsync(options);
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
