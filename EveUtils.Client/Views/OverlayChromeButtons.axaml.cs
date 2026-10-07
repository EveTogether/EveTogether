using System.Windows.Input;
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

    /// <summary>Whether the overlay has a compact view to switch to (ET-478). Off for the overlays that do not, so only
    /// the activity window carries the fourth button.</summary>
    public static readonly StyledProperty<bool> ShowCompactToggleProperty =
        AvaloniaProperty.Register<OverlayChromeButtons, bool>(nameof(ShowCompactToggle));

    /// <summary>Whether the overlay is in its compact view now: the button then offers the way back.</summary>
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<OverlayChromeButtons, bool>(nameof(IsCompact));

    public static readonly StyledProperty<ICommand?> ToggleCompactCommandProperty =
        AvaloniaProperty.Register<OverlayChromeButtons, ICommand?>(nameof(ToggleCompactCommand));

    public static readonly StyledProperty<string> CompactToggleTipProperty =
        AvaloniaProperty.Register<OverlayChromeButtons, string>(nameof(CompactToggleTip), "Compact view");

    public bool ShowCompactToggle
    {
        get => GetValue(ShowCompactToggleProperty);
        set => SetValue(ShowCompactToggleProperty, value);
    }

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public ICommand? ToggleCompactCommand
    {
        get => GetValue(ToggleCompactCommandProperty);
        set => SetValue(ToggleCompactCommandProperty, value);
    }

    public string CompactToggleTip
    {
        get => GetValue(CompactToggleTipProperty);
        private set => SetValue(CompactToggleTipProperty, value);
    }

    public OverlayChromeButtons() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCompactProperty)
            CompactToggleTip = IsCompact ? "Full view" : "Compact view";
    }

    private void OnClose(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as Window)?.Close();
}
