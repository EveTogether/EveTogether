using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace EveUtils.Client.Views;

/// <summary>
/// What every pop-out overlay is: a borderless window that sits over EVE. Pinnable, opacity-adjustable, dragged by
/// its header, resized from any edge, and remembering all four of those between sessions.
///
/// This started as <see cref="DpsOverlayWindow"/> and became a base class when the fleet overlay needed the same
/// behaviour (ET-72). It is shared rather than copied deliberately: the operator already knows how the DPS pop-out
/// behaves, and a second overlay that dragged, pinned or remembered itself even slightly differently would be a
/// second thing to learn for no reason. The derived window supplies its content, its <see cref="GeometryKey"/> and
/// nothing else.
/// </summary>
public abstract class OverlayWindow : Window
{
    public static readonly StyledProperty<double> FillOpacityProperty =
        AvaloniaProperty.Register<OverlayWindow, double>(nameof(FillOpacity), 0.9);

    /// <summary>How solid the backdrop is behind the readout. The text and any graph stay fully opaque whatever this
    /// is — only the fill fades, so turning the window down does not turn the figures down with it.</summary>
    public double FillOpacity
    {
        get => GetValue(FillOpacityProperty);
        set => SetValue(FillOpacityProperty, value);
    }

    private readonly SolidColorBrush _fillBrush = new(Color.Parse("#0B0F0D"));
    private readonly DispatcherTimer _saveDebounce;
    private Border? _backdrop;
    private PixelPoint _lastPosition;
    private bool _ready;

    /// <summary>The settings key this overlay's geometry is remembered under. An empty key means "do not remember",
    /// which is what a window with nothing to identify it (an unnamed tracker) gets.</summary>
    protected abstract string GeometryKey { get; }

    protected OverlayWindow()
    {
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); _ = PersistAsync(); };

        PositionChanged += (_, e) => { _lastPosition = e.Point; QueueSave(); };
    }

    /// <summary>Hand the base the border whose fill <see cref="FillOpacity"/> drives. Called from the derived
    /// constructor, right after <c>InitializeComponent</c>.</summary>
    protected void UseBackdrop(Border backdrop)
    {
        _backdrop = backdrop;
        _backdrop.Background = _fillBrush;
        _fillBrush.Opacity = FillOpacity;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowChrome.ApplySquareCorners(this);
        WindowChrome.EnableBorderlessResize(this);
        _lastPosition = Position;
        _ = RestoreAsync();
    }

    private async Task RestoreAsync()
    {
        var geometry = string.IsNullOrEmpty(GeometryKey) ? null : await OverlayGeometryStore.LoadAsync(GeometryKey);
        if (geometry is not null)
        {
            if (geometry.Width >= MinWidth) Width = geometry.Width;
            if (geometry.Height >= MinHeight) Height = geometry.Height;
            FillOpacity = Math.Clamp(geometry.Opacity, 0.15, 1);
            Topmost = geometry.Pinned;
            if (geometry.HasPosition) Position = new PixelPoint(geometry.X, geometry.Y);
        }

        _lastPosition = Position;
        _ready = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FillOpacityProperty)
        {
            _fillBrush.Opacity = FillOpacity;
            QueueSave();
        }
        else if (e.Property == TopmostProperty || e.Property == ClientSizeProperty)
        {
            QueueSave();
        }
    }

    private void QueueSave()
    {
        if (!_ready) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private Task PersistAsync() =>
        string.IsNullOrEmpty(GeometryKey)
            ? Task.CompletedTask
            : OverlayGeometryStore.SaveAsync(GeometryKey, new OverlayGeometry
            {
                HasPosition = true,
                X = _lastPosition.X,
                Y = _lastPosition.Y,
                Width = Bounds.Width,
                Height = Bounds.Height,
                Opacity = FillOpacity,
                Pinned = Topmost
            });

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _saveDebounce.Stop();
        if (_ready) _ = PersistAsync();
        base.OnClosing(e);
    }

    /// <summary>Start a window drag from a press on the header — unless the press landed on one of the header's own
    /// controls (opacity, PIN, close), which would otherwise drag the window instead of pressing the button.</summary>
    protected void BeginHeaderDrag(PointerPressedEventArgs e)
    {
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() == true)
            return;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
