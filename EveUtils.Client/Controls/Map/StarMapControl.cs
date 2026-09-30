using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using EveUtils.Shared.Modules.Map.Dtos;

namespace EveUtils.Client.Controls.Map;

/// <summary>
/// The zoomable New Eden map (ET-392, mockup v2 scene 1). The static map — jumps and system dots — is built once
/// per graph in world coordinates (<see cref="MapGeometry"/>) and drawn under one transform, so a frame costs a
/// few dozen draw calls rather than ~7k lines. What depends on the zoom (level of detail, labels, rings) is drawn
/// per frame for what is on screen only. Pointer lookups go through a spatial grid, never a scan of every system.
///
/// <para>Level of detail follows the zoom relative to "whole map fits": below 2.6× regions, below 7× constellations,
/// above that systems with their security.</para>
/// </summary>
public sealed class StarMapControl : Control
{
    private const double ConstellationsFrom = 2.6;
    private const double SystemsFrom = 7;
    private const double MinZoom = 0.6;
    private const double MaxZoom = 60;
    private const double FitMargin = 0.92;
    private const double SystemFocusZoom = 11;
    private const double FrameMaxZoom = 16;
    private const double FramePadding = 56;
    private const double FleetFramePadding = 40;
    private const double FleetMaxZoom = 14;
    private const double BadgeRadius = 9;
    private const double MarkerHitRadius = 8;
    private const double BadgeOffset = 10;
    private const double CommanderRingRadius = 10;
    private const double CommanderStarRadius = 8;
    private const double DragThreshold = 3;
    private const double SelectRadius = 12;
    private const double RegionsPickRadius = 7;
    private const double ConstellationsPickRadius = 9;
    private const double PopoverPadding = 9;
    private const double PopoverMaxWidth = 300;
    private const double PopoverFontSize = 12;
    private const double PopoverRowGap = 3;
    private const double WheelStep = 1.25;
    private const double DoubleTapZoom = 2.5;

    private const double TrailWidth = 2.5;
    private const double TrailTailOpacity = 0.2;

    private static readonly ImmutableDashStyle TrailStepDash = new([2, 1.4], 0);
    private static readonly ImmutableDashStyle TrailGapDash = new([0.6, 2.6], 0);
    private static readonly TimeSpan FlyDuration = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan ZoomDuration = TimeSpan.FromMilliseconds(250);
    private static readonly Cursor GrabCursor = new(StandardCursorType.Hand);
    private static readonly Cursor DragCursor = new(StandardCursorType.SizeAll);
    private static readonly IImmutableBrush CrossRegionBrush = new ImmutableSolidColorBrush(MapPalette.CrossRegionLine, 0.75);
    private static readonly IImmutableBrush RouteBrush = new ImmutableSolidColorBrush(MapPalette.Route);
    private static readonly IImmutableBrush GatelessBrush = new ImmutableSolidColorBrush(MapPalette.Muted, 0.5);
    private static readonly IImmutableBrush BackgroundBrush = new ImmutableSolidColorBrush(MapPalette.Background);
    private static readonly ImmutablePen NodeOutline = new(BackgroundBrush, 1.5);

    public static readonly StyledProperty<Thickness> ViewInsetProperty =
        AvaloniaProperty.Register<StarMapControl, Thickness>(nameof(ViewInset));

    public static readonly StyledProperty<MapGraphDto?> GraphProperty =
        AvaloniaProperty.Register<StarMapControl, MapGraphDto?>(nameof(Graph));

    /// <summary>The planned route as system indexes, start first; null for none.</summary>
    public static readonly StyledProperty<IReadOnlyList<int>?> RouteProperty =
        AvaloniaProperty.Register<StarMapControl, IReadOnlyList<int>?>(nameof(Route));

    public static readonly StyledProperty<IReadOnlyList<MapMarker>?> MarkersProperty =
        AvaloniaProperty.Register<StarMapControl, IReadOnlyList<MapMarker>?>(nameof(Markers));

    /// <summary>The clicked system's index, -1 for none. Two-way: a click sets it, the view model may too.</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<StarMapControl, int>(nameof(SelectedIndex), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<MapFocusRequest?> FocusRequestProperty =
        AvaloniaProperty.Register<StarMapControl, MapFocusRequest?>(nameof(FocusRequest));

    /// <summary>The followed character's trail, oldest system first; null for none.</summary>
    public static readonly StyledProperty<IReadOnlyList<MapTrailStep>?> TrailProperty =
        AvaloniaProperty.Register<StarMapControl, IReadOnlyList<MapTrailStep>?>(nameof(Trail));

    /// <summary>The system of the character the map follows, ringed; -1 for none.</summary>
    public static readonly StyledProperty<int> FollowIndexProperty =
        AvaloniaProperty.Register<StarMapControl, int>(nameof(FollowIndex), -1);

    /// <summary>Fleet members per system, drawn as numbered badges with who is there on hover; null for none.</summary>
    public static readonly StyledProperty<IReadOnlyList<MapFleetBadge>?> FleetBadgesProperty =
        AvaloniaProperty.Register<StarMapControl, IReadOnlyList<MapFleetBadge>?>(nameof(FleetBadges));

    /// <summary>What a badge's "12s ago" is measured against.</summary>
    public static readonly StyledProperty<TimeProvider> ClockProperty =
        AvaloniaProperty.Register<StarMapControl, TimeProvider>(nameof(Clock), TimeProvider.System);

    /// <summary>What the hover popover says about a system index; null for no popover (the lookup may also return null).</summary>
    public static readonly StyledProperty<Func<int, MapSystemInfo?>?> SystemInfoSourceProperty =
        AvaloniaProperty.Register<StarMapControl, Func<int, MapSystemInfo?>?>(nameof(SystemInfoSource));

    /// <summary>Raised by the owner when what <see cref="SystemInfoSource"/> answers changed (a jump count arrived); an open popover rereads it.</summary>
    public static readonly StyledProperty<int> InfoRevisionProperty =
        AvaloniaProperty.Register<StarMapControl, int>(nameof(InfoRevision));

    public static readonly DirectProperty<StarMapControl, double> ZoomLevelProperty =
        AvaloniaProperty.RegisterDirect<StarMapControl, double>(nameof(ZoomLevel), map => map.ZoomLevel);

    public static readonly DirectProperty<StarMapControl, MapDetailLevel> DetailLevelProperty =
        AvaloniaProperty.RegisterDirect<StarMapControl, MapDetailLevel>(nameof(DetailLevel), map => map.DetailLevel);

    public static readonly DirectProperty<StarMapControl, string> DetailLevelLabelProperty =
        AvaloniaProperty.RegisterDirect<StarMapControl, string>(nameof(DetailLevelLabel), map => map.DetailLevelLabel);

    private readonly MapLabelGrid _labels = new();
    private readonly MapLabelCache _labelImages = new();

    private MapGeometry? _geometry;
    private MapHitGrid? _hitGrid;
    private StreamGeometry? _routeGeometry;
    private HashSet<int> _routeSystems = [];
    private IImmutableBrush[] _softLineBrushes = [];
    private IImmutableBrush[] _strongLineBrushes = [];
    private IImmutableBrush[] _dotBrushes = [];

    private double _centerX;
    private double _centerY;
    private double _scale = 1;
    private double _fitScale = 1;
    private bool _hasView;
    private MapFocusRequest? _pendingFocus;
    private MapFocusRequest? _followFraming;

    private Point? _pressedAt;
    private Point _pressedCenter;
    private bool _isDragging;
    private int _hoverIndex = -1;
    private Flight? _flight;
    private CancellationTokenSource? _popoverDelay;
    private Popover? _popover;

    private double _zoomLevel = 1;
    private MapDetailLevel _detailLevel = MapDetailLevel.Regions;
    private string _detailLevelLabel = "REGIONS";

    static StarMapControl()
    {
        AffectsRender<StarMapControl>(ViewInsetProperty, GraphProperty, RouteProperty, MarkersProperty, SelectedIndexProperty, TrailProperty, FollowIndexProperty,
            FleetBadgesProperty);
        ClipToBoundsProperty.OverrideDefaultValue<StarMapControl>(true);
        FocusableProperty.OverrideDefaultValue<StarMapControl>(true);
    }

    public StarMapControl()
    {
        Cursor = GrabCursor;
        DoubleTapped += (_, e) =>
        {
            _RaiseViewMovedByUser();
            Point at = e.GetPosition(this);
            _FlyTo(_ToWorldX(at.X), _ToWorldY(at.Y), _ClampScale(_scale * DoubleTapZoom), ZoomDuration);
        };
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The edges of the control that overlays cover: the map is framed and centred in what is left.</summary>
    public Thickness ViewInset
    {
        get => GetValue(ViewInsetProperty);
        set => SetValue(ViewInsetProperty, value);
    }

    public MapGraphDto? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public IReadOnlyList<int>? Route
    {
        get => GetValue(RouteProperty);
        set => SetValue(RouteProperty, value);
    }

    public IReadOnlyList<MapMarker>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public MapFocusRequest? FocusRequest
    {
        get => GetValue(FocusRequestProperty);
        set => SetValue(FocusRequestProperty, value);
    }

    public IReadOnlyList<MapTrailStep>? Trail
    {
        get => GetValue(TrailProperty);
        set => SetValue(TrailProperty, value);
    }

    public int FollowIndex
    {
        get => GetValue(FollowIndexProperty);
        set => SetValue(FollowIndexProperty, value);
    }

    public IReadOnlyList<MapFleetBadge>? FleetBadges
    {
        get => GetValue(FleetBadgesProperty);
        set => SetValue(FleetBadgesProperty, value);
    }

    public TimeProvider Clock
    {
        get => GetValue(ClockProperty);
        set => SetValue(ClockProperty, value);
    }

    public Func<int, MapSystemInfo?>? SystemInfoSource
    {
        get => GetValue(SystemInfoSourceProperty);
        set => SetValue(SystemInfoSourceProperty, value);
    }

    public int InfoRevision
    {
        get => GetValue(InfoRevisionProperty);
        set => SetValue(InfoRevisionProperty, value);
    }

    /// <summary>How long the pointer rests on a system before its popover opens.</summary>
    public TimeSpan PopoverDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The open popover's lines, one per row; null while none is open.</summary>
    internal string? PopoverText => _popover?.Text;

    /// <summary>Where the open popover sits inside the map; empty while none is open.</summary>
    internal Rect PopoverBounds => _popover?.Bounds ?? default;

    internal double PickRadius => _PickRadius();

    /// <summary>The system the open popover is about, -1 while none is open.</summary>
    internal int PopoverIndex => _popover?.SystemIndex ?? -1;

    /// <summary>The pilot moved the view: a drag, the wheel, a double tap or the zoom buttons. Never raised for a
    /// <see cref="FocusRequest"/> — that is the map moving itself, and following must not pause on its own moves.</summary>
    public event EventHandler? ViewMovedByUser;

    /// <summary>Zoom relative to the whole map fitting the control: 1 = all of New Eden.</summary>
    public double ZoomLevel
    {
        get => _zoomLevel;
        private set => SetAndRaise(ZoomLevelProperty, ref _zoomLevel, value);
    }

    public MapDetailLevel DetailLevel
    {
        get => _detailLevel;
        private set => SetAndRaise(DetailLevelProperty, ref _detailLevel, value);
    }

    public string DetailLevelLabel
    {
        get => _detailLevelLabel;
        private set => SetAndRaise(DetailLevelLabelProperty, ref _detailLevelLabel, value);
    }

    public void ZoomBy(double factor)
    {
        _RaiseViewMovedByUser();
        _FlyTo(_centerX, _centerY, _ClampScale(_scale * factor), ZoomDuration);
    }

    public void ZoomToFit()
    {
        _RaiseViewMovedByUser();
        if (Graph is { } graph)
            _FlyTo(graph.Width / 2, graph.Height / 2, _fitScale, FlyDuration);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GraphProperty)
            _OnGraphChanged();
        else if (change.Property == ViewInsetProperty)
            _OnViewSizeChanged();
        else if (change.Property == RouteProperty)
            _OnRouteChanged();
        else if (change.Property == FocusRequestProperty)
        {
            _followFraming = null;
            if (FocusRequest is { } request)
                _Focus(request);
        }
        else if (change.Property == FleetBadgesProperty || change.Property == MarkersProperty || change.Property == RouteProperty
                 || change.Property == InfoRevisionProperty || change.Property == SystemInfoSourceProperty)
            _RefreshPopover();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _OnViewSizeChanged();
    }

    // What following framed was measured against the view as it was then. The overlays and the layout settle after the
    // owner asked for that framing, so a followed group is framed again against the view it ended up with — until the pilot
    // moves the map themself.
    private void _OnViewSizeChanged()
    {
        _UpdateFit();
        if (_followFraming is { } framing)
            _Focus(framing);
    }

    private void _RaiseViewMovedByUser()
    {
        _followFraming = null;
        ViewMovedByUser?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _labelImages.Clear();
        _HidePopover();
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _flight = null;
        _HidePopover();
        _pressedAt = e.GetPosition(this);
        _pressedCenter = new Point(_centerX, _centerY);
        _isDragging = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point at = e.GetPosition(this);
        if (_pressedAt is { } pressed)
        {
            Vector moved = at - pressed;
            if (!_isDragging && Math.Abs(moved.X) + Math.Abs(moved.Y) > DragThreshold)
            {
                _isDragging = true;
                Cursor = DragCursor;
                _RaiseViewMovedByUser();
            }
            if (_isDragging)
            {
                _centerX = _pressedCenter.X - moved.X / _scale;
                _centerY = _pressedCenter.Y - moved.Y / _scale;
                _PublishView();
            }
            return;
        }

        int hover = _BadgeAt(at) ?? _NearestAt(at, _PickRadius());
        if (hover == _hoverIndex)
            return;
        _hoverIndex = hover;
        _HidePopover();
        if (hover >= 0)
            _ArmPopover();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressedAt is null)
            return;
        if (!_isDragging)
            SelectedIndex = _NearestAt(e.GetPosition(this), SelectRadius);
        _pressedAt = null;
        _isDragging = false;
        Cursor = GrabCursor;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _HidePopover();
        if (_hoverIndex < 0)
            return;
        _hoverIndex = -1;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _RaiseViewMovedByUser();
        _flight = null;
        Point at = e.GetPosition(this);
        double worldX = _ToWorldX(at.X), worldY = _ToWorldY(at.Y);
        _scale = _ClampScale(_scale * Math.Pow(WheelStep, e.Delta.Y));
        _centerX = worldX - (at.X - _ViewCentre.X) / _scale;
        _centerY = worldY - (at.Y - _ViewCentre.Y) / _scale;
        _PublishView();
        e.Handled = true;
    }

    // ── View state ───────────────────────────────────────────────────────────────────────────────

    private void _OnGraphChanged()
    {
        _hasView = false;
        _flight = null;
        _hoverIndex = -1;
        _HidePopover();
        _labelImages.Clear();
        MapGraphDto? graph = Graph;
        _geometry = graph is null ? null : new MapGeometry(graph);
        _hitGrid = graph is null ? null : new MapHitGrid(graph);
        _softLineBrushes = _RegionBrushes(graph, 0.55);
        _strongLineBrushes = _RegionBrushes(graph, 0.8);
        _dotBrushes = graph is null
            ? []
            : [.. graph.Regions.Select(region => region.HasGates ? new ImmutableSolidColorBrush(_RegionColour(region)) : GatelessBrush)];
        _OnRouteChanged();
        _UpdateFit();
    }

    private void _OnRouteChanged()
    {
        _routeSystems = Route is { } route ? [.. route] : [];
        _routeGeometry = null;
        if (Graph is not { } graph || Route is not { Count: > 1 } steps)
            return;

        _routeGeometry = new StreamGeometry();
        using StreamGeometryContext context = _routeGeometry.Open();
        MapSystemDto first = graph.Systems[steps[0]];
        context.BeginFigure(new Point(first.X, first.Y), false);
        foreach (int step in steps.Skip(1))
            context.LineTo(new Point(graph.Systems[step].X, graph.Systems[step].Y));
        context.EndFigure(false);
    }

    private void _UpdateFit()
    {
        if (Graph is not { } graph || _ViewSize.Width <= 0 || _ViewSize.Height <= 0)
            return;

        _fitScale = Math.Min(_ViewSize.Width / graph.Width, _ViewSize.Height / graph.Height) * FitMargin;
        if (!_hasView || _scale < _fitScale * 0.5)
        {
            _centerX = graph.Width / 2;
            _centerY = graph.Height / 2;
            _scale = _fitScale;
            _hasView = true;
        }
        _scale = _ClampScale(_scale);
        _PublishView();

        if (_pendingFocus is { } pending)
        {
            _pendingFocus = null;
            _Focus(pending);
        }
    }

    private void _PublishView()
    {
        // The map moved under a resting pointer (drag, wheel, flight, resize): what was hovered no longer is.
        _hoverIndex = -1;
        _HidePopover();
        ZoomLevel = _scale / _fitScale;
        DetailLevel = ZoomLevel < ConstellationsFrom ? MapDetailLevel.Regions
            : ZoomLevel < SystemsFrom ? MapDetailLevel.Constellations
            : MapDetailLevel.Systems;
        DetailLevelLabel = DetailLevel switch
        {
            MapDetailLevel.Regions => "REGIONS",
            MapDetailLevel.Constellations => "CONSTELLATIONS",
            _ => "SYSTEMS"
        };
        InvalidateVisual();
    }

    private void _Focus(MapFocusRequest request)
    {
        if (Graph is not { } graph || !_hasView)
        {
            _pendingFocus = request;
            return;
        }

        _followFraming = request.Framing == MapFraming.Fleet || request.MinZoom is not null ? request : null;

        List<MapSystemDto> systems = request.SystemIndexes
            .Where(index => index >= 0 && index < graph.Systems.Count)
            .Select(index => graph.Systems[index])
            .ToList();
        if (systems.Count == 0)
            return;
        if (request.Framing == MapFraming.Fleet)
        {
            double left = systems.Min(s => s.X), right = systems.Max(s => s.X), top = systems.Min(s => s.Y), bottom = systems.Max(s => s.Y);
            _FlyTo((left + right) / 2, (top + bottom) / 2, FleetFrameScale(right - left, bottom - top, _ViewSize, _fitScale), FlyDuration);
            return;
        }
        if (systems.Count == 1)
        {
            _FlyTo(systems[0].X, systems[0].Y, Math.Max(_scale, _fitScale * (request.MinZoom ?? SystemFocusZoom)), FlyDuration);
            return;
        }

        double minX = systems.Min(s => s.X), maxX = systems.Max(s => s.X), minY = systems.Min(s => s.Y), maxY = systems.Max(s => s.Y);
        double scale = Math.Min((_ViewSize.Width - FramePadding * 2) / Math.Max(maxX - minX, 1),
                                (_ViewSize.Height - FramePadding * 2) / Math.Max(maxY - minY, 1));
        _FlyTo((minX + maxX) / 2, (minY + maxY) / 2, Math.Clamp(scale, _fitScale, _fitScale * FrameMaxZoom), FlyDuration);
    }

    private void _FlyTo(double x, double y, double scale, TimeSpan duration)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            (_centerX, _centerY, _scale) = (x, y, scale);
            _PublishView();
            return;
        }

        var flight = new Flight(_centerX, _centerY, _scale, x, y, scale, duration, Stopwatch.StartNew());
        _flight = flight;
        topLevel.RequestAnimationFrame(_ => _Advance(topLevel, flight));
    }

    private void _Advance(TopLevel topLevel, Flight flight)
    {
        if (!ReferenceEquals(_flight, flight))
            return;

        double progress = Math.Min(1, flight.Clock.Elapsed / flight.Duration);
        double eased = 1 - Math.Pow(1 - progress, 3);
        _centerX = flight.FromX + (flight.ToX - flight.FromX) * eased;
        _centerY = flight.FromY + (flight.ToY - flight.FromY) * eased;
        _scale = Math.Exp(Math.Log(flight.FromScale) + (Math.Log(flight.ToScale) - Math.Log(flight.FromScale)) * eased);
        _PublishView();

        if (progress < 1)
            topLevel.RequestAnimationFrame(_ => _Advance(topLevel, flight));
        else
            _flight = null;
    }

    // The follow-fleet zoom for members spread over spanX × spanY world units: the extent fills the view less a 40 px
    // margin, clamped between the whole map and 14×. It reads the 2D extent only — jumps are no measure of map
    // distance — and can only shrink as either span grows.
    internal static double FleetFrameScale(double spanX, double spanY, Size viewport, double fitScale)
    {
        double scale = Math.Min((viewport.Width - FleetFramePadding * 2) / Math.Max(spanX, 1),
                                (viewport.Height - FleetFramePadding * 2) / Math.Max(spanY, 1));
        return Math.Clamp(scale, fitScale, fitScale * FleetMaxZoom);
    }

    internal Point ScreenPointOf(int systemIndex) =>
        Graph is { } graph && systemIndex >= 0 && systemIndex < graph.Systems.Count ? _ToScreen(graph.Systems[systemIndex]) : default;

    internal Point CommanderPointOf(int systemIndex) =>
        Graph is { } graph && systemIndex >= 0 && systemIndex < graph.Systems.Count ? _CommanderCentre(_ToScreen(graph.Systems[systemIndex])) : default;

    internal Point MarkerPointOf(int systemIndex) =>
        Graph is { } graph && systemIndex >= 0 && systemIndex < graph.Systems.Count ? _MarkerCentre(_ToScreen(graph.Systems[systemIndex])) : default;

    internal string? MarkerLabelOf(int systemIndex) =>
        Markers?.Where(marker => marker.SystemIndex == systemIndex).Select(marker => marker.Label).ToList() is { Count: > 0 } names
            ? _MarkerLabelText(systemIndex, names)
            : null;

    internal Size MarkerLabelSizeOf(int systemIndex) =>
        MarkerLabelOf(systemIndex) is { } text ? _Label(text, MapLabelFont.Marker, _AccentColour()).Size : default;

    // The badge, the commander's star and the diamond of your own characters all open the popover of their system.
    private int? _BadgeAt(Point at)
    {
        if (Graph is not { } graph)
            return null;
        foreach (MapFleetBadge badge in (FleetBadges ?? []).Where(badge => badge.SystemIndex >= 0 && badge.SystemIndex < graph.Systems.Count))
        {
            if (_IsWithin(at, _BadgeCentre(_ToScreen(graph.Systems[badge.SystemIndex])), BadgeRadius))
                return badge.SystemIndex;
        }
        foreach (MapMarker marker in (Markers ?? []).Where(marker => marker.SystemIndex >= 0 && marker.SystemIndex < graph.Systems.Count))
        {
            if (_IsWithin(at, _MarkerCentre(_ToScreen(graph.Systems[marker.SystemIndex])), MarkerHitRadius))
                return marker.SystemIndex;
        }
        return null;
    }

    private static bool _IsWithin(Point at, Point centre, double radius) =>
        Math.Abs(at.X - centre.X) <= radius && Math.Abs(at.Y - centre.Y) <= radius;

    // ── Hover popover (ET-399) ───────────────────────────────────────────────────────────────────

    // The pick radius follows the zoom: dots are 1.3 px at regions, 2.4 at constellations and up to 7 at systems, and a
    // fixed 10 px is too generous among the dense dots of the first two and too tight around a big system dot.
    private double _PickRadius() => DetailLevel switch
    {
        MapDetailLevel.Regions => RegionsPickRadius,
        MapDetailLevel.Constellations => ConstellationsPickRadius,
        _ => Math.Min(12, _NodeRadius(DetailLevel) + 5)
    };

    private double _NodeRadius(MapDetailLevel level) => level switch
    {
        MapDetailLevel.Regions => 1.3,
        MapDetailLevel.Constellations => 2.4,
        _ => Math.Min(7, 3 + ZoomLevel / 6)
    };

    private void _ArmPopover()
    {
        if (SystemInfoSource is null)
            return;
        if (PopoverDelay <= TimeSpan.Zero)
        {
            _RefreshPopover(force: true);
            return;
        }

        _popoverDelay = new CancellationTokenSource();
        _ = _OpenAfterDelayAsync(_popoverDelay.Token);
    }

    private async Task _OpenAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(PopoverDelay, Clock, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        _RefreshPopover(force: true);
    }

    private void _HidePopover()
    {
        _popoverDelay?.Cancel();
        _popoverDelay = null;
        if (_popover is null)
            return;
        _popover = null;
        InvalidateVisual();
    }

    // Builds (or, while one is open, rebuilds) the popover of the hovered system from the owner's snapshot — nothing here
    // reads the SDE or the database. Without force it only refreshes a popover that is already open.
    private void _RefreshPopover(bool force = false)
    {
        if (!force && _popover is null)
            return;
        if (_hoverIndex < 0 || Graph is not { } graph || _hoverIndex >= graph.Systems.Count || SystemInfoSource?.Invoke(_hoverIndex) is not { } info)
        {
            _HidePopover();
            return;
        }

        // A long list names fewer people rather than outgrow the map: one name less until it fits.
        int occupants = Math.Min(MapPopoverRows.MaxOccupants, info.Here.Count);
        (List<FormattedText> lines, List<string> text, Size size) = _MeasurePopover(info, occupants);
        while (size.Height > Bounds.Height && occupants > 1)
            (lines, text, size) = _MeasurePopover(info, --occupants);

        _popover = new Popover(_hoverIndex, lines, string.Join('\n', text),
            MapPopoverLayout.Place(_ToScreen(graph.Systems[_hoverIndex]), size, Bounds.Size));
        InvalidateVisual();
    }

    private (List<FormattedText> Lines, List<string> Text, Size Size) _MeasurePopover(MapSystemInfo info, int maxOccupants)
    {
        Color accent = _AccentColour();
        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty));
        var lines = new List<FormattedText>();
        var text = new List<string>();
        double width = 0, height = 0;
        foreach (IReadOnlyList<MapPopoverRun> row in MapPopoverRows.From(info, Clock.GetUtcNow(), accent, maxOccupants))
        {
            string content = MapPopoverRows.TextOf(row);
            var line = new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, PopoverFontSize,
                new ImmutableSolidColorBrush(MapPalette.Text)) { MaxTextWidth = PopoverMaxWidth - PopoverPadding * 2 };
            int start = 0;
            foreach (MapPopoverRun run in row)
            {
                line.SetForegroundBrush(new ImmutableSolidColorBrush(run.Colour), start, run.Text.Length);
                if (run.IsBold)
                    line.SetFontWeight(FontWeight.Bold, start, run.Text.Length);
                start += run.Text.Length;
            }
            lines.Add(line);
            text.Add(content);
            width = Math.Max(width, line.Width);
            height += line.Height + (lines.Count > 1 ? PopoverRowGap : 0);
        }

        return (lines, text, new Size(width + PopoverPadding * 2, height + PopoverPadding * 2));
    }

    private void _DrawPopover(DrawingContext context)
    {
        if (_popover is not { } popover)
            return;

        context.DrawRectangle(MapPalette.HudBackgroundBrush, new ImmutablePen(new ImmutableSolidColorBrush(_AccentColour()), 1), popover.Bounds, 3, 3);
        double y = popover.Bounds.Y + PopoverPadding;
        foreach (FormattedText line in popover.Lines)
        {
            context.DrawText(line, new Point(popover.Bounds.X + PopoverPadding, y));
            y += line.Height + PopoverRowGap;
        }
    }

    private Color _AccentColour() =>
        this.TryFindResource("AccentBrightBrush", ActualThemeVariant, out object? found) && found is ISolidColorBrush brush ? brush.Color : MapPalette.Text;

    private static Point _BadgeCentre(Point system) => new(system.X + BadgeOffset, system.Y - BadgeOffset);

    private static Point _MarkerCentre(Point system) => new(system.X - 12, system.Y + 2);

    private static Point _CommanderCentre(Point system) => new(system.X - BadgeOffset, system.Y - BadgeOffset);

    private double _ClampScale(double scale) => Math.Clamp(scale, _fitScale * MinZoom, _fitScale * MaxZoom);

    private int _NearestAt(Point at, double radiusPixels) =>
        _hitGrid?.Nearest(_ToWorldX(at.X), _ToWorldY(at.Y), radiusPixels / _scale) ?? -1;

    // The part of the control the map is framed and centred in: the control less the overlays that sit on it.
    private Size _ViewSize => new(Math.Max(0, Bounds.Width - ViewInset.Left - ViewInset.Right),
                                  Math.Max(0, Bounds.Height - ViewInset.Top - ViewInset.Bottom));

    private Point _ViewCentre => new(ViewInset.Left + _ViewSize.Width / 2, ViewInset.Top + _ViewSize.Height / 2);

    private double _ToWorldX(double screenX) => (screenX - _ViewCentre.X) / _scale + _centerX;

    private double _ToWorldY(double screenY) => (screenY - _ViewCentre.Y) / _scale + _centerY;

    private Point _ToScreen(MapSystemDto system) =>
        new((system.X - _centerX) * _scale + _ViewCentre.X, (system.Y - _centerY) * _scale + _ViewCentre.Y);

    private Point _ToScreen(double x, double y) =>
        new((x - _centerX) * _scale + _ViewCentre.X, (y - _centerY) * _scale + _ViewCentre.Y);

    // ── Drawing ──────────────────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var area = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, area);
        if (Graph is not { } graph || _geometry is not { } geometry || !_hasView)
            return;

        Color accent = _AccentColour();
        MapDetailLevel level = DetailLevel;
        double margin = 40 / _scale;
        var world = new Rect(_ToWorldX(0) - margin, _ToWorldY(0) - margin,
            area.Width / _scale + margin * 2, area.Height / _scale + margin * 2);

        _DrawNetwork(context, graph, geometry, level, world);
        double nodeRadius = _NodeRadius(level);
        _DrawSystems(context, graph, level, world, nodeRadius);

        _DrawTrail(context, graph, area, accent);
        _labels.Clear();
        _DrawMarkers(context, graph, level, world, accent);
        _DrawFleetBadges(context, graph, world, accent);
        if (FollowIndex >= 0 && FollowIndex < graph.Systems.Count)
            context.DrawEllipse(null, new ImmutablePen(new ImmutableSolidColorBrush(accent), 2.5), _ToScreen(graph.Systems[FollowIndex]),
                nodeRadius + 10, nodeRadius + 10);
        foreach (int ring in new[] { SelectedIndex, _hoverIndex }.Where(index => index >= 0 && index < graph.Systems.Count))
            context.DrawEllipse(null, new ImmutablePen(new ImmutableSolidColorBrush(accent), 2), _ToScreen(graph.Systems[ring]), nodeRadius + 5, nodeRadius + 5);

        _DrawRegionLabels(context, graph, level, area);
        if (level != MapDetailLevel.Regions)
            _DrawConstellationLabels(context, graph, level, area);
        if (level == MapDetailLevel.Systems)
            _DrawSystemLabels(context, graph, world, nodeRadius);
        _DrawPopover(context);
    }

    private void _DrawNetwork(DrawingContext context, MapGraphDto graph, MapGeometry geometry, MapDetailLevel level, Rect world)
    {
        double linePixels = level switch
        {
            MapDetailLevel.Regions => 0.8,
            MapDetailLevel.Constellations => 1.1,
            _ => 1.6
        };
        double lineWidth = linePixels / _scale;
        // A dash is measured in pen thicknesses, so dividing by the on-screen width keeps it in pixels at any zoom.
        ImmutableDashStyle? crossConstellationDash = level == MapDetailLevel.Regions ? null : new([4 / linePixels, 3 / linePixels], 0);
        var crossRegionPen = new ImmutablePen(CrossRegionBrush, lineWidth, new ImmutableDashStyle([6 / linePixels, 4 / linePixels], 0));
        double dotWidth = (level == MapDetailLevel.Regions ? 2.6 : 4.8) / _scale;

        using (context.PushTransform(Matrix.CreateTranslation(-_centerX, -_centerY)
                                     * Matrix.CreateScale(_scale, _scale)
                                     * Matrix.CreateTranslation(_ViewCentre.X, _ViewCentre.Y)))
        {
            List<MapRegionDto> visible = graph.Regions
                .Where(region => world.Intersects(new Rect(region.MinX, region.MinY, region.MaxX - region.MinX, region.MaxY - region.MinY)))
                .ToList();
            foreach (MapRegionDto region in visible)
            {
                IImmutableBrush inside = level == MapDetailLevel.Regions ? _softLineBrushes[region.Index] : _strongLineBrushes[region.Index];
                context.DrawGeometry(null, new ImmutablePen(inside, lineWidth), geometry.InConstellation[region.Index]);
                context.DrawGeometry(null, new ImmutablePen(_softLineBrushes[region.Index], lineWidth, crossConstellationDash),
                    geometry.CrossConstellation[region.Index]);
            }
            context.DrawGeometry(null, crossRegionPen, geometry.CrossRegion);

            if (_routeGeometry is not null)
                context.DrawGeometry(null, new ImmutablePen(RouteBrush, (level == MapDetailLevel.Systems ? 4 : 3) / _scale,
                    lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), _routeGeometry);

            if (level == MapDetailLevel.Systems)
                return;
            foreach (MapRegionDto region in visible)
                context.DrawGeometry(null, new ImmutablePen(_dotBrushes[region.Index], dotWidth, lineCap: PenLineCap.Round),
                    geometry.Dots[region.Index]);
        }
    }

    // System view draws each visible system in its security colour; the lower levels already drew region-coloured
    // dots in one call per region, so only the route's systems get a bigger dot on top.
    private void _DrawSystems(DrawingContext context, MapGraphDto graph, MapDetailLevel level, Rect world, double nodeRadius)
    {
        if (level == MapDetailLevel.Systems)
        {
            foreach (MapSystemDto system in graph.Systems)
            {
                if (!world.Contains(new Point(system.X, system.Y)))
                    continue;
                double radius = _routeSystems.Contains(system.Index) ? nodeRadius + 1.5 : nodeRadius;
                context.DrawEllipse(MapPalette.SecurityBrush(system.DisplaySecurity), NodeOutline, _ToScreen(system), radius, radius);
            }
            return;
        }

        foreach (int index in _routeSystems)
        {
            MapSystemDto system = graph.Systems[index];
            if (world.Contains(new Point(system.X, system.Y)))
                context.DrawEllipse(_dotBrushes[system.RegionIndex], null, _ToScreen(system), nodeRadius + 1.5, nodeRadius + 1.5);
        }
    }

    // Drawn in screen space so the dashes keep their size at any zoom. Older jumps fade towards the tail; a jump the
    // trail could not follow (see MapTrailStep.IsGapBefore) is dotted rather than dashed.
    private void _DrawTrail(DrawingContext context, MapGraphDto graph, Rect area, Color accent)
    {
        if (Trail is not { Count: > 1 } steps)
            return;

        int jumps = steps.Count - 1;
        for (int at = 1; at < steps.Count; at++)
        {
            int from = steps[at - 1].SystemIndex, to = steps[at].SystemIndex;
            if (from < 0 || from >= graph.Systems.Count || to < 0 || to >= graph.Systems.Count)
                continue;

            Point start = _ToScreen(graph.Systems[from]), end = _ToScreen(graph.Systems[to]);
            if (!area.Inflate(TrailWidth).Intersects(new Rect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
                    Math.Abs(start.X - end.X), Math.Abs(start.Y - end.Y))))
                continue;

            double opacity = jumps == 1 ? 1 : TrailTailOpacity + (1 - TrailTailOpacity) * (at - 1) / (jumps - 1);
            var pen = new ImmutablePen(new ImmutableSolidColorBrush(accent, opacity), TrailWidth,
                steps[at].IsGapBefore ? TrailGapDash : TrailStepDash);
            context.DrawLine(pen, start, end);
        }
    }

    private void _DrawMarkers(DrawingContext context, MapGraphDto graph, MapDetailLevel level, Rect world, Color accent)
    {
        if (Markers is not { Count: > 0 } markers)
            return;

        var outline = new ImmutablePen(new ImmutableSolidColorBrush(accent), 2);
        foreach (IGrouping<int, MapMarker> here in markers.Where(m => m.SystemIndex >= 0 && m.SystemIndex < graph.Systems.Count).GroupBy(m => m.SystemIndex))
        {
            MapSystemDto system = graph.Systems[here.Key];
            if (!world.Contains(new Point(system.X, system.Y)))
                continue;

            Point at = _ToScreen(system);
            Point centre = _MarkerCentre(at);
            const double half = 4.25;
            var diamond = new StreamGeometry();
            using (StreamGeometryContext shape = diamond.Open())
            {
                shape.BeginFigure(new Point(centre.X, centre.Y - half), true);
                shape.LineTo(new Point(centre.X + half, centre.Y));
                shape.LineTo(new Point(centre.X, centre.Y + half));
                shape.LineTo(new Point(centre.X - half, centre.Y));
                shape.EndFigure(true);
            }
            context.DrawGeometry(BackgroundBrush, outline, diamond);
            _labels.Reserve(new Rect(centre.X - half - 2, centre.Y - half - 2, half * 2 + 4, half * 2 + 4));

            if (level != MapDetailLevel.Regions)
                _DrawLabel(context, _MarkerLabelText(here.Key, here.Select(m => m.Label).ToList()), MapLabelFont.Marker, accent, new Point(at.X, at.Y - 22), 2);
        }
    }

    private string _MarkerLabelText(int systemIndex, IReadOnlyList<string> names) =>
        MapMarkerLabel.For(names, FleetBadges?.FirstOrDefault(badge => badge.SystemIndex == systemIndex)?.Commander?.Name);

    // A filled AccentBright disc with the member count in the map's background colour, up and to the right of the
    // system so it never covers your own diamond on its left.
    private void _DrawFleetBadges(DrawingContext context, MapGraphDto graph, Rect world, Color accent)
    {
        if (FleetBadges is not { Count: > 0 } badges)
            return;

        var fill = new ImmutableSolidColorBrush(accent);
        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, FontWeight.Bold);
        foreach (MapFleetBadge badge in badges.Where(badge => badge.SystemIndex >= 0 && badge.SystemIndex < graph.Systems.Count))
        {
            MapSystemDto system = graph.Systems[badge.SystemIndex];
            if (!world.Contains(new Point(system.X, system.Y)))
                continue;

            Point centre = _BadgeCentre(_ToScreen(system));
            context.DrawEllipse(fill, NodeOutline, centre, BadgeRadius, BadgeRadius);
            var count = new FormattedText(badge.Members.Count.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 11, BackgroundBrush);
            context.DrawText(count, new Point(centre.X - count.Width / 2, centre.Y - count.Height / 2));
            _labels.Reserve(new Rect(centre.X - BadgeRadius - 1, centre.Y - BadgeRadius - 1, BadgeRadius * 2 + 2, BadgeRadius * 2 + 2));

            if (badge.Commander is { } commander)
                _DrawCommander(context, _CommanderCentre(_ToScreen(system)), commander.Name, accent);
        }
    }

    // The fleet commander: a five-pointed star inside a ring, up and to the left of the system where the count badge is
    // up and to the right, so the two sit side by side and one never hides the other. The star and the ring are shapes,
    // not colours, so it reads without telling hues apart. The name goes above it where the labels leave room.
    private void _DrawCommander(DrawingContext context, Point centre, string name, Color accent)
    {
        var ink = new ImmutableSolidColorBrush(accent);
        context.DrawEllipse(BackgroundBrush, new ImmutablePen(ink, 1.5), centre, CommanderRingRadius, CommanderRingRadius);

        var star = new StreamGeometry();
        using (StreamGeometryContext shape = star.Open())
        {
            for (int point = 0; point < 10; point++)
            {
                double radius = point % 2 == 0 ? CommanderStarRadius : CommanderStarRadius * 0.45;
                double angle = -Math.PI / 2 + point * Math.PI / 5;
                var corner = new Point(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle));
                if (point == 0)
                    shape.BeginFigure(corner, true);
                else
                    shape.LineTo(corner);
            }
            shape.EndFigure(true);
        }
        context.DrawGeometry(ink, null, star);
        _labels.Reserve(new Rect(centre.X - CommanderRingRadius - 1, centre.Y - CommanderRingRadius - 1,
            CommanderRingRadius * 2 + 2, CommanderRingRadius * 2 + 2));

        if (DetailLevel != MapDetailLevel.Regions)
            _DrawLabel(context, name, MapLabelFont.Marker, accent, new Point(centre.X, centre.Y - CommanderRingRadius - 8), 2);
    }

    private void _DrawRegionLabels(DrawingContext context, MapGraphDto graph, MapDetailLevel level, Rect area)
    {
        MapLabelFont font = level == MapDetailLevel.Regions ? MapLabelFont.Region : MapLabelFont.RegionLarge;
        foreach (MapRegionDto region in graph.Regions)
        {
            Point at = _ToScreen(region.CenterX, region.CenterY);
            if (at.X < -100 || at.X > area.Width + 100 || at.Y < -30 || at.Y > area.Height + 30)
                continue;

            string name = string.Join(' ', region.Name.ToUpperInvariant().ToCharArray()) + (region.HasGates ? string.Empty : " · no gates");
            bool placed = _DrawLabel(context, name, font, region.HasGates ? _RegionColour(region) : MapPalette.Muted, at, 4);
            if (placed && level == MapDetailLevel.Regions && region.FactionName is { } faction)
                _DrawLabel(context, faction, MapLabelFont.Faction, MapPalette.Muted, new Point(at.X, at.Y + 14), 2);
        }
    }

    private void _DrawConstellationLabels(DrawingContext context, MapGraphDto graph, MapDetailLevel level, Rect area)
    {
        foreach (MapConstellationDto constellation in graph.Constellations)
        {
            Point at = _ToScreen(constellation.CenterX, constellation.CenterY);
            if (at.X < -80 || at.X > area.Width + 80 || at.Y < -20 || at.Y > area.Height + 20)
                continue;
            Color ink = level == MapDetailLevel.Systems ? MapPalette.Muted : _RegionColour(graph.Regions[constellation.RegionIndex]);
            _DrawLabel(context, constellation.Name, MapLabelFont.Constellation, ink,
                new Point(at.X, at.Y - (level == MapDetailLevel.Systems ? 18 : 0)), 2);
        }
    }

    // Route, own characters and the selection claim their label first; the rest of what is on screen fills the gaps.
    private void _DrawSystemLabels(DrawingContext context, MapGraphDto graph, Rect world, double nodeRadius)
    {
        IEnumerable<int> important = _routeSystems
            .Concat(Markers?.Select(marker => marker.SystemIndex) ?? [])
            .Append(SelectedIndex)
            .Where(index => index >= 0 && index < graph.Systems.Count);
        IEnumerable<int> order = important.Concat(graph.Systems.Select(system => system.Index));
        var seen = new HashSet<int>();

        foreach (int index in order)
        {
            if (!seen.Add(index))
                continue;
            MapSystemDto system = graph.Systems[index];
            if (!world.Contains(new Point(system.X, system.Y)))
                continue;

            Point at = _ToScreen(system);
            MapLabel name = _Label(system.Name + " ", MapLabelFont.SystemName, MapPalette.Text);
            MapLabel security = _Label(system.DisplaySecurity.ToString("0.0", CultureInfo.InvariantCulture), MapLabelFont.SystemSecurity,
                MapPalette.SecurityColour(system.DisplaySecurity));
            double width = name.Size.Width + security.Size.Width, y = at.Y + nodeRadius + 9, left = at.X - width / 2;
            if (!_labels.TryPlace(new Rect(left - 2, y - 8, width + 4, 16)))
                continue;
            name.Draw(context, new Point(left, y - name.Size.Height / 2));
            security.Draw(context, new Point(left + name.Size.Width, y - security.Size.Height / 2));
        }
    }

    private bool _DrawLabel(DrawingContext context, string content, MapLabelFont font, Color colour, Point centre, double padding)
    {
        MapLabel label = _Label(content, font, colour);
        var origin = new Point(centre.X - label.Size.Width / 2, centre.Y - label.Size.Height / 2);
        if (!_labels.TryPlace(new Rect(origin.X - padding, origin.Y - padding, label.Size.Width + padding * 2, label.Size.Height + padding * 2)))
            return false;
        label.Draw(context, origin);
        return true;
    }

    private MapLabel _Label(string content, MapLabelFont font, Color colour) =>
        _labelImages.Get(content, font, colour, GetValue(TextElement.FontFamilyProperty), TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);

    private static IImmutableBrush[] _RegionBrushes(MapGraphDto? graph, double opacity) => graph is null
        ? []
        : [.. graph.Regions.Select(region => new ImmutableSolidColorBrush(_RegionColour(region), opacity))];

    private static Color _RegionColour(MapRegionDto region) =>
        MapPalette.Regions[region.ColourIndex % MapPalette.Regions.Count];

    private sealed record Popover(int SystemIndex, IReadOnlyList<FormattedText> Lines, string Text, Rect Bounds);

    private sealed record Flight(
        double FromX, double FromY, double FromScale, double ToX, double ToY, double ToScale, TimeSpan Duration, Stopwatch Clock);
}
