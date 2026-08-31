using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// The Appraisal tool (ET-83), hosted like the other feature modules: a docked tab when docked, its own window when
/// floating. Below a narrow content width the paste column gives up some of its width, so the value columns stay
/// readable in the docked host.
/// </summary>
public partial class AppraisalWindow : ChromedWindow, IHostableModuleWindow
{
    public Action? CloseRequested { get; set; }

    private const double WideLayoutWidth = 760;

    private readonly Grid? _bodyGrid;
    private bool? _isWide;

    public AppraisalWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _bodyGrid = this.FindControl<Grid>("AppraisalBodyGrid");
        if (_bodyGrid is not null)
            _bodyGrid.SizeChanged += (_, e) => _ApplyViewport(e.NewSize);
    }

    public AppraisalWindow(AppraisalViewModel viewModel) : this() => DataContext = viewModel;

    private void _ApplyViewport(Size size)
    {
        if (_bodyGrid is null)
            return;

        var wide = size.Width >= WideLayoutWidth;
        if (wide == _isWide)
            return;

        _isWide = wide;
        _bodyGrid.ColumnDefinitions = new ColumnDefinitions(wide ? "300,12,*" : "220,10,*");
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (CloseRequested is not null) CloseRequested();
        else Close();
    }
}
