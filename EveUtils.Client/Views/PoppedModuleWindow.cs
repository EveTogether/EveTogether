using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace EveUtils.Client.Views;

/// <summary>
/// The window a module's view moves into when it is popped out of the docked host (ET-396): the standard chrome, the
/// module's own content, and its place and size remembered like the other pop-outs. It has no pop-in button of its own —
/// the module's header carries PUT BACK IN MAIN WINDOW, and closing the window does the same.
/// </summary>
public sealed class PoppedModuleWindow : ChromedWindow
{
    private readonly string _geometryKey;
    private readonly DispatcherTimer _saveDebounce;
    private PixelPoint _lastPosition;
    private bool _ready;

    public PoppedModuleWindow(string title, string geometryKey, Control content, Size size, Size minimumSize)
    {
        _geometryKey = geometryKey;
        Title = title;
        Content = content;
        Width = size.Width;
        Height = size.Height;
        MinWidth = minimumSize.Width;
        MinHeight = minimumSize.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); _ = _PersistAsync(); };
        PositionChanged += (_, e) => { _lastPosition = e.Point; _QueueSave(); };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _lastPosition = Position;
        _ = _RestoreAsync();
    }

    // After Show() has centred the window: the first open keeps that, later ones jump to where it was left. A spot on a
    // monitor that has since been unplugged is not used, the window stays centred instead.
    private async Task _RestoreAsync()
    {
        OverlayGeometry? geometry = await OverlayGeometryStore.LoadAsync(_geometryKey);
        if (geometry is not null)
        {
            if (geometry.Width >= MinWidth) Width = geometry.Width;
            if (geometry.Height >= MinHeight) Height = geometry.Height;
            var remembered = new PixelPoint(geometry.X, geometry.Y);
            if (geometry.HasPosition && WindowChrome.IsPositionOnScreen(this, remembered))
                Position = remembered;
        }

        _lastPosition = Position;
        _ready = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ClientSizeProperty)
            _QueueSave();
    }

    private void _QueueSave()
    {
        if (!_ready) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private Task _PersistAsync() =>
        OverlayGeometryStore.SaveAsync(_geometryKey, new OverlayGeometry
        {
            HasPosition = true,
            X = _lastPosition.X,
            Y = _lastPosition.Y,
            Width = Bounds.Width,
            Height = Bounds.Height
        });

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _saveDebounce.Stop();
        if (_ready) _ = _PersistAsync();
        base.OnClosing(e);
    }
}
