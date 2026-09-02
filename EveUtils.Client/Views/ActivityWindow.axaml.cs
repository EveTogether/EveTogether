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
        // Only on a save that landed (ET-98). A failed one leaves the window standing with the reason on it, and a
        // group member's save never reaches this window — it is raised by the view model this window owns.
        viewModel.SaveSucceeded += Close;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Only once the window is up: a clock ticking for a window nobody opened is a clock nobody stops.
        _viewModel?.Start();
        if (_viewModel?.RunLoot is { } runLoot)
            await runLoot.RefreshAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.SaveSucceeded -= Close;

        _viewModel?.Dispose();
        base.OnClosed(e);
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e) => BeginHeaderDrag(e);
}
