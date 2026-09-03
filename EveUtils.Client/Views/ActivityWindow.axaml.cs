using System;
using Avalonia.Controls;
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
    private bool _closeApproved;

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
        // Only when the run is done with: a save that landed, or a discard by the pilot who commands it (ET-155). A
        // failed one leaves the window standing with the reason on it, and a group member's save never reaches this
        // window — it is raised by the view model this window owns.
        viewModel.CloseRequested += _CloseFromViewModel;
    }

    /// <summary>The view model is already done deciding, so this close skips the question below rather than asking
    /// again about a run it just saved or threw away.</summary>
    private void _CloseFromViewModel()
    {
        _closeApproved = true;
        Close();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_viewModel is null)
            return;

        // The window's own state before the clock runs on it: the remembered weather and tier, whose run this is,
        // and the run the store already has open. Nothing called this until now — the window went up on a
        // constructor's worth of state, so it forgot the tier, could not say whose run it was, and offered a START
        // for a run it was already in, which then adopted the old one instead of beginning a new one.
        await _viewModel.LoadAsync();
        // Only once the window is up: a clock ticking for a window nobody opened is a clock nobody stops.
        _viewModel.Start();
    }

    /// <summary>
    /// A run outlives its window in the store, so closing has to decide what becomes of it — a close that decided
    /// nothing left the row open and the next window adopted it, start time, site and commander's group code
    /// included (Raymond, ten reports, 2026-09-03). The answer is the view model's; this only holds the window
    /// still while it is being given.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_viewModel is null || _closeApproved)
            return;

        e.Cancel = true;
        bool mayClose = await _viewModel.RequestCloseAsync();
        if (_closeApproved)
            return;   // saving inside the question already closed this window

        if (!mayClose)
            return;   // "don't close after all"

        _closeApproved = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.CloseRequested -= _CloseFromViewModel;

        _viewModel?.Dispose();
        base.OnClosed(e);
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e) => BeginHeaderDrag(e);
}
