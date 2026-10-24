using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.ViewModels.Map;

namespace EveUtils.Client.Views;

/// <summary>
/// The MAP module (ET-392), hosted like RUNS: a docked tab when docked, its own window when floating. The zoom
/// buttons only move this view, so they talk to the map control directly rather than through the view model.
/// </summary>
public partial class MapWindow : ChromedWindow
{
    public MapWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("SetDestinationButton")?.AddHandler(PointerPressedEvent, OnSetDestinationPressed, RoutingStrategies.Tunnel);
    }

    public MapWindow(MapViewModel viewModel) : this()
    {
        DataContext = viewModel;
        MapView?.ViewMovedByUser += (_, _) => viewModel.PauseFollow();
        Closed += (_, _) => viewModel.Dispose();
    }

    // Click carries no key state, so the Shift held at press time decides between the destination and a waypoint.
    private bool _shiftHeld;

    private void OnSetDestinationPressed(object? sender, PointerPressedEventArgs e) =>
        _shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

    private void OnSetDestination(object? sender, RoutedEventArgs e)
    {
        bool addWaypoint = _shiftHeld;
        _shiftHeld = false;
        if (DataContext is not MapViewModel map)
            return;
        if (addWaypoint)
            map.AddWaypointCommand.Execute(null);
        else
            map.SetDestinationCommand.Execute(null);
    }

    private StarMapControl? MapView => this.FindControl<StarMapControl>("Map");

    private void OnZoomIn(object? sender, RoutedEventArgs e) => MapView?.ZoomBy(2);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => MapView?.ZoomBy(0.5);

    private void OnZoomAll(object? sender, RoutedEventArgs e) => MapView?.ZoomToFit();
}
