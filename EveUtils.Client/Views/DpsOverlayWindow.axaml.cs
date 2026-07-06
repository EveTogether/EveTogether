using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Views;

/// <summary>
/// Borderless, pinnable, opacity-adjustable per-character DPS overlay. Reuses the shared
/// <see cref="WindowChrome"/> (square corners + edge resize) and the <c>DpsGraph</c> control (line smoothing
/// on). Position/size/opacity/pin are persisted per character via <see cref="DpsOverlayStore"/>.
/// </summary>
public partial class DpsOverlayWindow : Window
{
    public static readonly StyledProperty<double> FillOpacityProperty =
        AvaloniaProperty.Register<DpsOverlayWindow, double>(nameof(FillOpacity), 0.9);

    public double FillOpacity
    {
        get => GetValue(FillOpacityProperty);
        set => SetValue(FillOpacityProperty, value);
    }

    private readonly SolidColorBrush _fillBrush = new(Color.Parse("#0B0F0D"));
    private readonly DispatcherTimer _saveDebounce;
    private string _character = string.Empty;
    private PixelPoint _lastPosition;
    private bool _ready;

    public DpsOverlayWindow()
    {
        InitializeComponent();

        Backdrop.Background = _fillBrush;
        _fillBrush.Opacity = FillOpacity;

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); _ = PersistAsync(); };

        PositionChanged += (_, e) => { _lastPosition = e.Point; QueueSave(); };
    }

    public DpsOverlayWindow(DpsViewModel tracker) : this()
    {
        DataContext = tracker;
        _character = tracker.Character;
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
        var geometry = await DpsOverlayStore.LoadAsync(_character);
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
        DpsOverlayStore.SaveAsync(_character, new DpsOverlayGeometry
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

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        // Don't start a window drag when the press lands on a header control (opacity / PIN / close).
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() == true)
            return;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Click the graph to bring this character's EVE client to the front (eve-o-preview style). The probe matches by
    // window title, so it only works while that client is logged in as this character; otherwise it's a no-op.
    private void OnActivateClient(object? sender, TappedEventArgs e)
    {
        if (string.IsNullOrEmpty(_character))
            return;
        Program.Services?.GetService<Platform.IEveClientProbe>()?.Activate(_character);
    }
}
