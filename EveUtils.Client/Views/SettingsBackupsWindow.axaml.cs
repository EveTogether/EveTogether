using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// The backups window (ET-67), hosted like the other feature modules: a docked tab when docked, its own window when
/// floating. Below a narrow content width the list gives up some of its column, so the contents of a backup stay
/// readable in the docked host — which is the whole reason this left the sync tool.
/// </summary>
public partial class SettingsBackupsWindow : ChromedWindow, IHostableModuleWindow
{
    public Action? CloseRequested { get; set; }

    private const double WideLayoutWidth = 780;

    private Grid? _bodyGrid;
    private bool? _isWide;

    public SettingsBackupsWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _bodyGrid = this.FindControl<Grid>("BackupsBodyGrid");
        if (_bodyGrid is not null)
            _bodyGrid.SizeChanged += (_, e) => _ApplyViewport(e.NewSize);
    }

    public SettingsBackupsWindow(SettingsBackupsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void _ApplyViewport(Size size)
    {
        if (_bodyGrid is null)
            return;

        var wide = size.Width >= WideLayoutWidth;
        if (wide == _isWide)
            return;

        _isWide = wide;
        _bodyGrid.ColumnDefinitions = new ColumnDefinitions(wide ? "320,12,*" : "230,10,*");
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (CloseRequested is not null) CloseRequested();
        else Close();
    }
}
