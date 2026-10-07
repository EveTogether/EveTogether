using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using EveUtils.Client.Input;
using EveUtils.Client.ViewModels.Activity;
using Microsoft.Extensions.DependencyInjection;

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
    // The compact views have a width of their own and grow in height with what they show (ET-478). The size the pilot
    // gave the full window is kept apart, so the compact shape neither replaces it nor is lost when going back.
    private const double CardWidth = 360;
    private const double HudWidth = 720;
    private const double CompactMinHeight = 34;

    private readonly ActivityWindowViewModel? _viewModel;
    private readonly Size _fullMinSize;
    private Size _fullSize;
    private double? _compactWidth;
    private bool _closeApproved;

    protected override string GeometryKey => OverlayGeometryStore.ForActivity();

    protected override Size PersistedSize => _compactWidth is null ? base.PersistedSize : _fullSize;

    /// <summary>The compact window is anchored by its right edge, so what is remembered is where the full window would
    /// stand with that same right edge.</summary>
    protected override PixelPoint PersistedPosition(PixelPoint position) => _compactWidth is null
        ? position
        : new PixelPoint(position.X + (int)Math.Round((Bounds.Width - _fullSize.Width) * RenderScaling), position.Y);

    public ActivityWindow()
    {
        InitializeComponent();
        UseBackdrop(Backdrop);
        _fullMinSize = new Size(MinWidth, MinHeight);
        _fullSize = new Size(Width, Height);
    }

    public ActivityWindow(ActivityWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += _OnViewModelChanged;
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

    private void _OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ActivityWindowViewModel.IsCompact) or nameof(ActivityWindowViewModel.CompactStyle))
            _ApplyShape();
    }

    /// <summary>What was remembered is the full window's geometry, so a window that restores into the compact view is
    /// laid out again from that.</summary>
    protected override void OnGeometryRestored(bool wasRemembered)
    {
        base.OnGeometryRestored(wasRemembered);
        if (wasRemembered)
        {
            _fullSize = new Size(Width, Height);
            _compactWidth = null;
        }

        _ApplyShape();
    }

    /// <summary>Takes the window to the shape the view model asks for. The right edge stays where it was, so the
    /// toggle button is still under the pointer afterwards and the window does not jump.</summary>
    private void _ApplyShape()
    {
        if (_viewModel is null)
            return;

        double? wanted = !_viewModel.IsCompact ? null
            : _viewModel.CompactStyle is CompactRunStyle.Hud ? HudWidth : CardWidth;
        if (wanted == _compactWidth)
            return;

        double currentWidth = double.IsNaN(Width) ? Bounds.Width : Width;
        int rightEdge = Position.X + (int)Math.Round(currentWidth * RenderScaling);
        if (_compactWidth is null && wanted is not null)
            _fullSize = new Size(currentWidth, double.IsNaN(Height) ? Bounds.Height : Height);

        _compactWidth = wanted;
        if (wanted is { } width)
        {
            CanResize = false;
            MinWidth = width;
            MinHeight = CompactMinHeight;
            Width = width;
            Height = double.NaN;
            SizeToContent = SizeToContent.Height;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            CanResize = true;
            MinWidth = _fullMinSize.Width;
            MinHeight = _fullMinSize.Height;
            Width = _fullSize.Width;
            Height = _fullSize.Height;
        }

        double newWidth = wanted ?? _fullSize.Width;
        Position = new PixelPoint(rightEdge - (int)Math.Round(newWidth * RenderScaling), Position.Y);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= _CloseFromViewModel;
            _viewModel.PropertyChanged -= _OnViewModelChanged;
        }

        _viewModel?.Dispose();
        base.OnClosed(e);
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e) => BeginHeaderDrag(e);

    /// <summary>The compact views' own header and line are drag handles like the full view's header.</summary>
    internal void BeginDrag(PointerPressedEventArgs e) => BeginHeaderDrag(e);

    // ET-319: Ctrl+Shift+S saves the running run, the exact route the SAVE button's own Command already takes — not
    // a second save path. While ET-320's global registration holds this same combination, Windows delivers it here
    // as WM_HOTKEY instead, so this handler only ever sees the key while the combination is not (or cannot be)
    // claimed system-wide — with focus is exactly then the one path left, never a second one racing the global fire.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _viewModel is null) return;

        var registry = Program.Services?.GetService<KeyboardShortcutRegistry>();
        if (registry is null || !registry.TryResolve(new KeyGesture(e.Key, e.KeyModifiers), out var action)) return;
        if (action != ShortcutAction.SaveRun) return;

        _viewModel.SaveRunFromShortcut();
        e.Handled = true;
    }
}
