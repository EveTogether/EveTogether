using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.ViewModels.Map;

namespace EveUtils.Client.Views;

/// <summary>
/// The fleet's map in Fleet metrics (ET-395): a second <see cref="StarMapControl"/> driven by its own
/// <see cref="MapViewModel"/>. Moving the map yourself pauses following, as in the MAP tab.
/// </summary>
public partial class FleetMapCard : UserControl
{
    public static readonly StyledProperty<ICommand?> OpenInMapCommandProperty =
        AvaloniaProperty.Register<FleetMapCard, ICommand?>(nameof(OpenInMapCommand));

    public static readonly StyledProperty<ICommand?> PopOutCommandProperty =
        AvaloniaProperty.Register<FleetMapCard, ICommand?>(nameof(PopOutCommand));

    public FleetMapCard()
    {
        AvaloniaXamlLoader.Load(this);
        MapView?.ViewMovedByUser += (_, _) => (DataContext as MapViewModel)?.PauseFollow();
        LayoutUpdated += (_, _) => _UpdateMapInset();
    }

    /// <summary>OPEN IN MAP: the screen the card sits on knows which fleet it is for.</summary>
    public ICommand? OpenInMapCommand
    {
        get => GetValue(OpenInMapCommandProperty);
        set => SetValue(OpenInMapCommandProperty, value);
    }

    /// <summary>POP OUT: the map in its own window, following the screen's fleet.</summary>
    public ICommand? PopOutCommand
    {
        get => GetValue(PopOutCommandProperty);
        set => SetValue(PopOutCommandProperty, value);
    }

    private StarMapControl? MapView => this.FindControl<StarMapControl>("CardMap");

    // The header and the footer sit on the map; it frames and centres in the part between them.
    private void _UpdateMapInset()
    {
        if (MapView is { } map && this.FindControl<Border>("HeaderBar") is { } header && this.FindControl<Border>("FooterBar") is { } footer)
            map.ViewInset = new Thickness(0, header.Bounds.Height, 0, footer.Bounds.Height);
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => MapView?.ZoomBy(2);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => MapView?.ZoomBy(0.5);
}
