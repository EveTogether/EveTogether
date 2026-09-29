using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
    private const double DragThreshold = 3;
    private const double SelectRadius = 12;
    private const double HoverRadius = 10;
    private const double WheelStep = 1.25;
    private const double DoubleTapZoom = 2.5;

    private static readonly TimeSpan FlyDuration = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan ZoomDuration = TimeSpan.FromMilliseconds(250);
    private static readonly Cursor GrabCursor = new(StandardCursorType.Hand);
    private static readonly Cursor DragCursor = new(StandardCursorType.SizeAll);
    private static readonly IImmutableBrush CrossRegionBrush = new ImmutableSolidColorBrush(MapPalette.CrossRegionLine, 0.75);
    private static readonly IImmutableBrush RouteBrush = new ImmutableSolidColorBrush(MapPalette.Route);
    private static readonly IImmutableBrush GatelessBrush = new ImmutableSolidColorBrush(MapPalette.Muted, 0.5);
    private static readonly IImmutableBrush BackgroundBrush = new ImmutableSolidColorBrush(MapPalette.Background);
    private static readonly ImmutablePen NodeOutline = new(BackgroundBrush, 1.5);

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

    private Point? _pressedAt;
    private Point _pressedCenter;
    private bool _isDragging;
    private int _hoverIndex = -1;
    private Flight? _flight;

    private double _zoomLevel = 1;
    private MapDetailLevel _detailLevel = MapDetailLevel.Regions;
    private string _detailLevelLabel = "REGIONS";

    static StarMapControl()
    {
        AffectsRender<StarMapControl>(GraphProperty, RouteProperty, MarkersProperty, SelectedIndexProperty);
        ClipToBoundsProperty.OverrideDefaultValue<StarMapControl>(true);
        FocusableProperty.OverrideDefaultValue<StarMapControl>(true);
    }

    public StarMapControl()
    {
        Cursor = GrabCursor;
        DoubleTapped += (_, e) =>
        {
            Point at = e.GetPosition(this);
            _FlyTo(_ToWorldX(at.X), _ToWorldY(at.Y), _ClampScale(_scale * DoubleTapZoom), ZoomDuration);
        };
        ResourcesChanged += (_, _) => InvalidateVisual();
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

    public void ZoomBy(double factor) => _FlyTo(_centerX, _centerY, _ClampScale(_scale * factor), ZoomDuration);

    public void ZoomToFit()
    {
        if (Graph is { } graph)
            _FlyTo(graph.Width / 2, graph.Height / 2, _fitScale, FlyDuration);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GraphProperty)
            _OnGraphChanged();
        else if (change.Property == RouteProperty)
            _OnRouteChanged();
        else if (change.Property == FocusRequestProperty && FocusRequest is { } request)
            _Focus(request);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _UpdateFit();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _labelImages.Clear();
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _flight = null;
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
            }
            if (_isDragging)
            {
                _centerX = _pressedCenter.X - moved.X / _scale;
                _centerY = _pressedCenter.Y - moved.Y / _scale;
                _PublishView();
            }
            return;
        }

        int hover = _NearestAt(at, HoverRadius);
        if (hover == _hoverIndex)
            return;
        _hoverIndex = hover;
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
        if (_hoverIndex < 0)
            return;
        _hoverIndex = -1;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _flight = null;
        Point at = e.GetPosition(this);
        double worldX = _ToWorldX(at.X), worldY = _ToWorldY(at.Y);
        _scale = _ClampScale(_scale * Math.Pow(WheelStep, e.Delta.Y));
        _centerX = worldX - (at.X - Bounds.Width / 2) / _scale;
        _centerY = worldY - (at.Y - Bounds.Height / 2) / _scale;
        _PublishView();
        e.Handled = true;
    }

    // ── View state ───────────────────────────────────────────────────────────────────────────────

    private void _OnGraphChanged()
    {
        _hasView = false;
        _flight = null;
        _hoverIndex = -1;
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
        if (Graph is not { } graph || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        _fitScale = Math.Min(Bounds.Width / graph.Width, Bounds.Height / graph.Height) * FitMargin;
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

        List<MapSystemDto> systems = request.SystemIndexes
            .Where(index => index >= 0 && index < graph.Systems.Count)
            .Select(index => graph.Systems[index])
            .ToList();
        if (systems.Count == 0)
            return;
        if (systems.Count == 1)
        {
            _FlyTo(systems[0].X, systems[0].Y, Math.Max(_scale, _fitScale * SystemFocusZoom), FlyDuration);
            return;
        }

        double minX = systems.Min(s => s.X), maxX = systems.Max(s => s.X), minY = systems.Min(s => s.Y), maxY = systems.Max(s => s.Y);
        double scale = Math.Min((Bounds.Width - FramePadding * 2) / Math.Max(maxX - minX, 1),
                                (Bounds.Height - FramePadding * 2) / Math.Max(maxY - minY, 1));
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

    private double _ClampScale(double scale) => Math.Clamp(scale, _fitScale * MinZoom, _fitScale * MaxZoom);

    private int _NearestAt(Point at, double radiusPixels) =>
        _hitGrid?.Nearest(_ToWorldX(at.X), _ToWorldY(at.Y), radiusPixels / _scale) ?? -1;

    private double _ToWorldX(double screenX) => (screenX - Bounds.Width / 2) / _scale + _centerX;

    private double _ToWorldY(double screenY) => (screenY - Bounds.Height / 2) / _scale + _centerY;

    private Point _ToScreen(MapSystemDto system) =>
        new((system.X - _centerX) * _scale + Bounds.Width / 2, (system.Y - _centerY) * _scale + Bounds.Height / 2);

    private Point _ToScreen(double x, double y) =>
        new((x - _centerX) * _scale + Bounds.Width / 2, (y - _centerY) * _scale + Bounds.Height / 2);

    // ── Drawing ──────────────────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var area = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, area);
        if (Graph is not { } graph || _geometry is not { } geometry || !_hasView)
            return;

        Color accent = this.TryFindResource("AccentBrightBrush", ActualThemeVariant, out object? found) && found is ISolidColorBrush brush
            ? brush.Color
            : MapPalette.Text;
        MapDetailLevel level = DetailLevel;
        double margin = 40 / _scale;
        var world = new Rect(_ToWorldX(0) - margin, _ToWorldY(0) - margin,
            area.Width / _scale + margin * 2, area.Height / _scale + margin * 2);

        _DrawNetwork(context, graph, geometry, level, world);
        double nodeRadius = level switch
        {
            MapDetailLevel.Regions => 1.3,
            MapDetailLevel.Constellations => 2.4,
            _ => Math.Min(7, 3 + ZoomLevel / 6)
        };
        _DrawSystems(context, graph, level, world, nodeRadius);

        _labels.Clear();
        _DrawMarkers(context, graph, level, world, accent);
        foreach (int ring in new[] { SelectedIndex, _hoverIndex }.Where(index => index >= 0 && index < graph.Systems.Count))
            context.DrawEllipse(null, new ImmutablePen(new ImmutableSolidColorBrush(accent), 2), _ToScreen(graph.Systems[ring]), nodeRadius + 5, nodeRadius + 5);

        _DrawRegionLabels(context, graph, level, area);
        if (level != MapDetailLevel.Regions)
            _DrawConstellationLabels(context, graph, level, area);
        if (level == MapDetailLevel.Systems)
            _DrawSystemLabels(context, graph, world, nodeRadius);
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
                                     * Matrix.CreateTranslation(Bounds.Width / 2, Bounds.Height / 2)))
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
            var centre = new Point(at.X - 12, at.Y + 2);
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
                _DrawLabel(context, string.Join(", ", here.Select(m => m.Label)), MapLabelFont.Marker, accent, new Point(at.X, at.Y - 22), 2);
        }
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

    private sealed record Flight(
        double FromX, double FromY, double FromScale, double ToX, double ToY, double ToScale, TimeSpan Duration, Stopwatch Clock);
}
