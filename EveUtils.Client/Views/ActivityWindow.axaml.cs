using System;
using Avalonia.Input;
using EveUtils.Client.ViewModels.Activity;

namespace EveUtils.Client.Views;

/// <summary>
/// The activity window (ET-98): a run you are still flying, laid over the game. Raymond chose the overlay shape over
/// a module in the shell, so it shares its whole chrome with the DPS and fleet pop-outs through
/// <see cref="OverlayWindow"/> — drag, edge resize, opacity, pin, and remembering all of it.
///
/// One remembered geometry for the window rather than one per run: unlike a fleet overlay there is only ever one of
/// these open, and where you want it is a property of your screen, not of tonight's run.
/// </summary>
public partial class ActivityWindow : OverlayWindow
{
    private readonly ActivityWindowViewModel? _viewModel;

    protected override string GeometryKey => OverlayGeometryStore.ForActivity();

    public ActivityWindow()
    {
        InitializeComponent();
        UseBackdrop(Backdrop);
    }

    public ActivityWindow(ActivityWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Only once the window is up: a clock ticking for a window nobody opened is a clock nobody stops.
        _viewModel?.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.Dispose();
        base.OnClosed(e);
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e) => BeginHeaderDrag(e);
}
