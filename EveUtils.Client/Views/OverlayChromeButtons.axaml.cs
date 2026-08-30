using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace EveUtils.Client.Views;

/// <summary>
/// The opacity / pin / close row every pop-out's title bar carries. One control, so the DPS pop-out and the fleet
/// overlay offer the same three buttons and cannot drift apart on how they look or what they do (ET-73).
///
/// It deliberately owns no state: <see cref="FillOpacity"/> is bound through to the window's own property by the
/// window that hosts it, the pin reads and writes <see cref="Window.Topmost"/> directly, and close closes the
/// window it is standing in. Nothing about a pop-out's behaviour or its remembered geometry passes through here.
/// </summary>
public partial class OverlayChromeButtons : UserControl
{
    /// <summary>How solid the host overlay's backdrop is. Bound two-way to <see cref="OverlayWindow.FillOpacity"/>
    /// by the hosting window, which is what persists it.</summary>
    public static readonly StyledProperty<double> FillOpacityProperty =
        AvaloniaProperty.Register<OverlayChromeButtons, double>(nameof(FillOpacity), 0.9,
            defaultBindingMode: BindingMode.TwoWay);

    public double FillOpacity
    {
        get => GetValue(FillOpacityProperty);
        set => SetValue(FillOpacityProperty, value);
    }

    public OverlayChromeButtons() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as Window)?.Close();
}
