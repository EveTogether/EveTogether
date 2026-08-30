using System;
using Avalonia.Input;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// The fleet pop-out (ET-72): the WITH FC ratio plus the two pilots an FC intervenes for, in a borderless window you
/// lay over EVE. Shares its whole chrome with the per-character DPS pop-out via <see cref="OverlayWindow"/> — drag,
/// edge resize, opacity, pin, and remembering all of it, here keyed per fleet.
/// </summary>
public partial class FleetOverlayWindow : OverlayWindow
{
    private readonly FleetOverlayViewModel? _viewModel;

    protected override string GeometryKey =>
        _viewModel is null ? string.Empty : OverlayGeometryStore.ForFleet(_viewModel.FleetId);

    public FleetOverlayWindow()
    {
        InitializeComponent();
        UseBackdrop(Backdrop);
    }

    public FleetOverlayWindow(FleetOverlayViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        Title = $"Fleet overlay — {viewModel.FleetName}";
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Only once the window is up: the readout is worked out on a timer, and a timer running for a window nobody
        // opened is a timer nobody stops.
        _viewModel?.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.Dispose();
        base.OnClosed(e);
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e) => BeginHeaderDrag(e);
}
