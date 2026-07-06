using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Views;

public partial class MainWindow : Window
{
    // The dragged character's id travels as a string (DataFormat<T> needs a reference type), mirroring the roster DnD.
    private static readonly DataFormat<string> CharacterFormat =
        DataFormat.CreateInProcessFormat<string>("eveutils-character-id");

    // Persisted "don't ask again" for the pop-outs-still-open exit confirmation.
    private const string SkipPopoutCloseConfirmKey = "close.skippopoutconfirm";
    private bool _confirmedClose;

    // Remembered shell widths per mode: the user's resize in each mode is restored on return,
    // instead of forcing fixed sizes. Rail-only stays the fixed rail width (a minimal launcher, not user-sized).
    private double _dockedWidth = 1100;
    private double _floatingWidth = 360;
    private const double RailOnlyWidth = 92;
    private bool _shellHooked;
    private bool _applyingShellState;   // guards our own Width writes from the resize-remember handler

    // Persist the window size across launches (WindowPlacementStore): the last Normal-state height + per-mode widths
    // + whether it was maximized are saved (debounced) and restored on the next start.
    private double _normalHeight = 720;     // last Normal-state height (matches the XAML default)
    private int _normalX, _normalY;         // last Normal-state screen position
    private bool _placementLoaded;          // true once the saved size is restored; gates saving until then
    private DispatcherTimer? _savePlacementDebounce;

    public MainWindow()
    {
        InitializeComponent();
        // Visually borderless; on Windows via extend-client-area so native snap (drag-to-edge, Win+arrows,
        // Snap Layouts) keeps working — see WindowChrome.ConfigureBorderless.
        WindowChrome.ConfigureBorderless(this);

        // Drag-to-reorder the character list. Scoped to the character panel (not the window) so it never clashes with
        // the roster's own drag-drop when that module is docked into this shell.
        CharacterList.AddHandler(PointerPressedEvent, OnCharacterPointerPressed, RoutingStrategies.Tunnel);
        CharacterList.AddHandler(DragDrop.DragOverEvent, OnCharacterDragOver);
        CharacterList.AddHandler(DragDrop.DropEvent, OnCharacterDrop);
    }

    // Begin a drag carrying the pressed character row's id. A press on a button (chart / expand / gear) or a local-only row (id 0,
    // not reorderable) starts no drag, so those interactions keep working. Avalonia 12 only lets a drag begin from a press.
    private async void OnCharacterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            return;

        var chain = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().ToList();
        if (chain is null || chain.Any(c => c is Button or ToggleButton or MenuItem))
            return;

        var characterId = chain.Select(c => (c.DataContext as CharacterViewModel)?.CharacterId)
            .FirstOrDefault(id => id is > 0);
        if (characterId is null)
            return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(CharacterFormat, characterId.Value.ToString(CultureInfo.InvariantCulture)));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        _HighlightCharacterTarget(null); // drag ended (dropped or cancelled) → clear the highlight
    }

    private void OnCharacterDragOver(object? sender, DragEventArgs e)
    {
        var isCharacter = e.DataTransfer.Contains(CharacterFormat);
        e.DragEffects = isCharacter ? DragDropEffects.Move : DragDropEffects.None;
        _HighlightCharacterTarget(isCharacter ? _CharacterRowUnder(e.Source as Visual) : null);
        e.Handled = true;
    }

    private void OnCharacterDrop(object? sender, DragEventArgs e)
    {
        var raw = e.DataTransfer.TryGetValue(CharacterFormat);
        if (DataContext is not MainWindowViewModel viewModel ||
            raw is null ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var draggedId))
            return;

        if (_CharacterRowUnder(e.Source as Visual)?.DataContext is CharacterViewModel { CharacterId: > 0 } target)
            _ = viewModel.ReorderCharacterAsync(draggedId, target.CharacterId);
        e.Handled = true;
    }

    // The row Border under a point, used both to resolve the drop target and to tint it mid-drag.
    private static Border? _CharacterRowUnder(Visual? source) =>
        source?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.DataContext is CharacterViewModel);

    private Border? _characterDropTarget;
    private IBrush? _characterDropOriginalBackground;

    // Tints the row the drop would land on so the target is obvious mid-drag. The row carries a local Background
    // (BgRowBrush), so the original is remembered and restored rather than cleared (clearing would fall back to the
    // panel style's brush, not the row brush).
    private void _HighlightCharacterTarget(Border? row)
    {
        if (ReferenceEquals(row, _characterDropTarget))
            return;
        if (_characterDropTarget is not null)
            _characterDropTarget.Background = _characterDropOriginalBackground;
        _characterDropTarget = row;
        if (_characterDropTarget is not null)
        {
            _characterDropOriginalBackground = _characterDropTarget.Background;
            if (this.TryFindResource("AccentSoftBrush", out var brush) && brush is IBrush accent)
                _characterDropTarget.Background = accent;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowChrome.ApplySquareCorners(this);     // angular EVE look — opt out of Windows 11 corner rounding
        WindowChrome.EnableBorderlessResize(this); // frameless window: restore edge/corner resize

        HookShellState();
        _ = RestorePlacementAsync(); // resume the size from the previous session (overrides the defaults applied above)

        // The window is shown now (so the modal has an owner): run the SDE update check independently of the
        // startup load chain, which can hang/fail on unrelated network steps.
        (DataContext as MainWindowViewModel)?.StartSdeUpdateCheck();
    }

    // The module shell has two responsive axes — DockMode (docked host vs. floating narrow shell) and the
    // collapsed character column — that drive the body grid's column tracks and the window width. Column widths can't
    // be compiled-bound on a non-visual ColumnDefinition, so they (and the floating resize) are applied here.
    private void HookShellState()
    {
        if (_shellHooked || DataContext is not MainWindowViewModel vm) return;
        _shellHooked = true;
        _normalX = Position.X;   // seed from the current placement so a save before any move is still correct
        _normalY = Position.Y;
        vm.PropertyChanged += OnShellPropertyChanged;
        PropertyChanged += OnWindowPlacementChanged;   // remember the user's per-mode resize + height + maximized
        PositionChanged += OnWindowPositionChanged;    // remember the user's window position
        ApplyShellState(vm);
    }

    // Remember the window's Normal-state position (skip our own restore writes + maximized/minimized states).
    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_applyingShellState || WindowState != WindowState.Normal) return;
        _normalX = Position.X;
        _normalY = Position.Y;
        QueueSavePlacement();
    }

    // Remember a user resize (per-mode width + shared height) and maximized state, then persist it (debounced). Skips
    // our own ApplyShellState writes and the fixed rail-only width.
    private void OnWindowPlacementChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_applyingShellState) return;

        if (e.Property == WidthProperty)
        {
            if (WindowState != WindowState.Normal || DataContext is not MainWindowViewModel vm || Width <= 200) return;
            if (!vm.IsFloating) _dockedWidth = Width;
            else if (!vm.IsCharsCollapsed) _floatingWidth = Width;   // rail-only is fixed, not remembered
            QueueSavePlacement();
        }
        else if (e.Property == HeightProperty)
        {
            if (WindowState != WindowState.Normal || Height <= 200) return;
            _normalHeight = Height;
            QueueSavePlacement();
        }
        else if (e.Property == WindowStateProperty)
        {
            // Persist maximized vs normal; ignore minimized so it doesn't overwrite the remembered maximized flag.
            if (WindowState is WindowState.Normal or WindowState.Maximized) QueueSavePlacement();
        }
    }

    // Restore the size persisted from the previous session, overriding the defaults HookShellState just applied.
    private async Task RestorePlacementAsync()
    {
        var placement = await WindowPlacementStore.LoadAsync();
        if (placement is not null)
        {
            if (placement.DockedWidth > 200) _dockedWidth = placement.DockedWidth;
            if (placement.FloatingWidth > 200) _floatingWidth = placement.FloatingWidth;
            if (placement.Height > 200) _normalHeight = placement.Height;

            _applyingShellState = true;
            Height = _normalHeight;
            // Restore the saved position only if it still lands on a connected monitor; otherwise leave the window
            // where CenterScreen put it (a removed/rearranged monitor must not strand it off-screen).
            if (placement.HasPosition && IsPositionOnScreen(placement.X, placement.Y))
            {
                Position = new PixelPoint(placement.X, placement.Y);
                _normalX = placement.X;
                _normalY = placement.Y;
            }
            _applyingShellState = false;

            if (DataContext is MainWindowViewModel vm) ApplyShellState(vm); // re-apply the remembered width for the mode
            if (placement.Maximized) WindowState = WindowState.Maximized;
        }
        _placementLoaded = true; // only now let user changes start saving (so restore itself never persists)
    }

    // True if the saved position keeps the window's title bar on a connected monitor (so it stays visible + grabbable).
    private bool IsPositionOnScreen(int x, int y)
    {
        var screens = Screens?.All;
        if (screens is null || screens.Count == 0) return false; // unknown screens → fall back to CenterScreen
        var titleBarPoint = new PixelPoint(x + 80, y + 16); // a spot on the title bar, inside the window
        return screens.Any(screen => screen.Bounds.Contains(titleBarPoint));
    }

    private void QueueSavePlacement()
    {
        if (!_placementLoaded) return;
        _savePlacementDebounce ??= CreateSaveTimer();
        _savePlacementDebounce.Stop();
        _savePlacementDebounce.Start();
    }

    private DispatcherTimer CreateSaveTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => { timer.Stop(); _ = WindowPlacementStore.SaveAsync(BuildPlacement()); };
        return timer;
    }

    private WindowPlacement BuildPlacement() => new()
    {
        DockedWidth = _dockedWidth,
        FloatingWidth = _floatingWidth,
        Height = _normalHeight,
        Maximized = WindowState == WindowState.Maximized,
        HasPosition = true,
        X = _normalX,
        Y = _normalY
    };

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MainWindowViewModel vm &&
            (e.PropertyName is nameof(MainWindowViewModel.IsFloating) or nameof(MainWindowViewModel.IsCharsCollapsed)))
            ApplyShellState(vm);
    }

    private void ApplyShellState(MainWindowViewModel vm)
    {
        var grid = this.FindControl<Grid>("BodyGrid");
        if (grid is null || grid.ColumnDefinitions.Count < 3) return;

        var railOnly = vm.IsFloating && vm.IsCharsCollapsed;
        var star = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[0].Width = railOnly ? star : GridLength.Auto;                 // rail
        grid.ColumnDefinitions[1].Width = vm.IsCharsCollapsed ? new GridLength(0)            // chars
            : vm.IsFloating ? star : new GridLength(250);
        grid.ColumnDefinitions[2].Width = vm.IsFloating ? new GridLength(0) : star;          // host

        // Rail-only: Windows enforces a minimum width on resizable window frames, which can exceed the rail's 92px —
        // let the rail stretch over whatever the OS gives us instead of leaving a dead strip beside itself.
        if (this.FindControl<Avalonia.Controls.Border>("Rail") is { } rail)
            rail.Width = railOnly ? double.NaN : 92;

        // Restore the remembered width for the mode we're entering (rail-only = the fixed rail width, no sliver).
        _applyingShellState = true;
        Width = vm.IsFloating ? (vm.IsCharsCollapsed ? RailOnlyWidth : _floatingWidth) : _dockedWidth;
        _applyingShellState = false;
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        // Let buttons in the title bar (window controls) handle their own clicks.
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() == true)
            return;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseWindow(object? sender, RoutedEventArgs e) => Close();

    // When pop-outs (DPS overlays / floating modules / info cards) are open, confirm before quitting — they are now
    // independent windows, so the user might not realise closing the main window takes them (and the app) down. The
    // confirmation can be silenced with a "don't ask again" opt-out. Closing always tears the pop-outs down so no
    // ownerless window keeps the app alive.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // Flush the current size so it resumes next launch, even if the close is held for the pop-out confirmation.
        if (_placementLoaded) _ = WindowPlacementStore.SaveAsync(BuildPlacement());

        if (_confirmedClose) return;

        // Program.Services is only wired in the real app (Program.Main), not in headless tests that new up a window.
        var dialogs = Program.Services?.GetService<DialogService>();
        if (dialogs is null || dialogs.OpenPopoutCount == 0) return; // nothing open → close normally

        e.Cancel = true; // hold the close synchronously; the async handler decides and re-closes
        _ = ConfirmCloseWithPopoutsAsync(dialogs);
    }

    private async Task ConfirmCloseWithPopoutsAsync(DialogService dialogs)
    {
        var settings = Program.Services.GetService<ISettingRepository>();

        if (!await IsPopoutCloseConfirmSilencedAsync(settings))
        {
            var (confirmed, optOut) = await dialogs.ConfirmWithOptOutAsync(
                "Close EVE Together",
                $"{dialogs.OpenPopoutCount} pop-out window(s) are still open. Close them and exit?",
                okText: "Close all",
                optOutText: "Don't ask me again");
            if (!confirmed) return; // stay open

            if (optOut && settings is not null)
                await settings.UpsertAsync(SkipPopoutCloseConfirmKey, "true");
        }

        dialogs.CloseAllPopouts();
        _confirmedClose = true;
        Close();
    }

    private static async Task<bool> IsPopoutCloseConfirmSilencedAsync(ISettingRepository? settings)
    {
        if (settings is null) return false;
        var all = await settings.ListAsync();
        return all.Any(s => s.Key == SkipPopoutCloseConfirmKey && s.Value == "true");
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
