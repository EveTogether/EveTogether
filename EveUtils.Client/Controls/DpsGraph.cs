using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace EveUtils.Client.Controls;

/// <summary>
/// Lightweight real-time scrolling line graph for live combat rates. Renders one polyline per series at a fixed time density —
/// a sample is always the same number of pixels wide (<see cref="PixelsPerSecond"/>), so a wider graph shows a longer
/// timeline instead of stretching the same window. The newest sample sits on the right ("now") and the curve scrolls
/// in from there. The owner mutates each series' values in place and bumps <see cref="Revision"/> to trigger a redraw.
/// Folded from the EVE-Utils demo (own code).
///
/// Lines are drawn in one lane per unit (ET-277): hp/s on top, GJ/s below, each with its own axis. Five lines in three
/// units on one auto-scaled axis made 46 GJ/s of neut a flat line under 1,000 dps. The GJ/s lane only takes room when
/// something is on it. A lane's scale is at least its <see cref="HitPointsMax"/> / <see cref="CapacitorMax"/> when the
/// owner sets one — a fleet screen gives every card the same — but never lower than the highest sample still on
/// screen (ET-280): the owner's shared scale can lag a peak that has not scrolled behind the left edge yet, and a
/// line must never flatten against the top edge.
/// </summary>
public sealed class DpsGraph : Control
{
    private static readonly IBrush GridBrush = new ImmutableSolidColorBrush(Color.Parse("#14FFFFFF"));
    private static readonly IBrush LaneDividerBrush = new ImmutableSolidColorBrush(Color.Parse("#26FFFFFF"));
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.Parse("#FF8A7E6B"));
    private static readonly Typeface LabelTypeface = new("Consolas");
    private static readonly IDashStyle GivenDash = new ImmutableDashStyle([3, 2.2], 0);

    // Below this height two lanes cannot both be read: the graph keeps the hp/s lane alone and leaves the GJ/s lines out
    // rather than put them back on the hp/s axis. The figures above the graph still carry them.
    private const double MinSplitHeight = 64;
    private const double LaneGap = 8;
    private const double CapacitorShare = 0.36;

    public static readonly StyledProperty<IReadOnlyList<DpsSeries>?> SeriesProperty =
        AvaloniaProperty.Register<DpsGraph, IReadOnlyList<DpsSeries>?>(nameof(Series));

    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<DpsGraph, int>(nameof(Revision));

    /// <summary>Horizontal time density: how many pixels one second of history occupies. The visible time span is
    /// therefore the plot width divided by this — wider graph, longer timeline.</summary>
    public static readonly StyledProperty<double> PixelsPerSecondProperty =
        AvaloniaProperty.Register<DpsGraph, double>(nameof(PixelsPerSecond), 18);

    /// <summary>How many seconds one sample represents — mirrors the ~30fps render driver, so px-per-sample =
    /// <see cref="PixelsPerSecond"/> × this.</summary>
    public static readonly StyledProperty<double> SecondsPerSampleProperty =
        AvaloniaProperty.Register<DpsGraph, double>(nameof(SecondsPerSample), 1.0 / 30.0);

    public static readonly StyledProperty<IReadOnlyList<GraphMarker>?> MarkersProperty =
        AvaloniaProperty.Register<DpsGraph, IReadOnlyList<GraphMarker>?>(nameof(Markers));

    /// <summary>The top of the hp/s lane; 0 = follow the samples on screen.</summary>
    public static readonly StyledProperty<double> HitPointsMaxProperty =
        AvaloniaProperty.Register<DpsGraph, double>(nameof(HitPointsMax));

    /// <summary>The top of the GJ/s lane; 0 = follow the samples on screen.</summary>
    public static readonly StyledProperty<double> CapacitorMaxProperty =
        AvaloniaProperty.Register<DpsGraph, double>(nameof(CapacitorMax));

    static DpsGraph()
    {
        AffectsRender<DpsGraph>(SeriesProperty, RevisionProperty, PixelsPerSecondProperty, SecondsPerSampleProperty,
            MarkersProperty, HitPointsMaxProperty, CapacitorMaxProperty);
    }

    public IReadOnlyList<DpsSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public double PixelsPerSecond
    {
        get => GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }

    public double SecondsPerSample
    {
        get => GetValue(SecondsPerSampleProperty);
        set => SetValue(SecondsPerSampleProperty, value);
    }

    public IReadOnlyList<GraphMarker>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    public double HitPointsMax
    {
        get => GetValue(HitPointsMaxProperty);
        set => SetValue(HitPointsMaxProperty, value);
    }

    public double CapacitorMax
    {
        get => GetValue(CapacitorMaxProperty);
        set => SetValue(CapacitorMaxProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        const double padLeft = 40, padTop = 8, padRight = 10, padBottom = 16; // bottom room for the time-axis labels
        var plot = new Rect(
            padLeft, padTop,
            Math.Max(0, bounds.Width - padLeft - padRight),
            Math.Max(0, bounds.Height - padTop - padBottom));

        if (plot.Width <= 0 || plot.Height <= 0)
            return;

        // One sample is always this many pixels wide; only the trailing samples that fit the width are drawn.
        var pxPerSample = Math.Max(0.01, PixelsPerSecond * SecondsPerSample);
        var visible = (int)Math.Ceiling(plot.Width / pxPerSample) + 2;

        var series = Series;
        // Never lower than what is actually about to be drawn (ET-280): the owner's scale (HitPointsMax/CapacitorMax,
        // e.g. the shared CombatScale) sets the floor, but a peak still on screen always wins over it.
        var hitPointsMax = NiceCeiling(Math.Max(HitPointsMax, ObservedMax(series, GraphLane.HitPoints, visible)), 100);
        var capacitorObserved = ObservedMax(series, GraphLane.Capacitor, visible);
        var splits = capacitorObserved >= 0.5 && plot.Height >= MinSplitHeight;

        var hitPointsLane = plot;
        var capacitorLane = default(Rect);
        if (splits)
        {
            var capacitorHeight = Math.Round((plot.Height - LaneGap) * CapacitorShare);
            hitPointsLane = new Rect(plot.X, plot.Y, plot.Width, plot.Height - LaneGap - capacitorHeight);
            capacitorLane = new Rect(plot.X, hitPointsLane.Bottom + LaneGap, plot.Width, capacitorHeight);
        }

        DrawTimeGrid(context, plot);
        // Split, the hp/s lane's "0" would sit right on top of the GJ/s lane's top figure; the baseline speaks for itself.
        DrawLaneGrid(context, hitPointsLane, hitPointsMax, "hp/s", hitPointsLane.Height >= 60 ? 4 : 2, labelsBaseline: !splits);

        var capacitorMax = NiceCeiling(Math.Max(CapacitorMax, capacitorObserved), 10);
        if (splits)
        {
            context.DrawLine(new Pen(LaneDividerBrush, 1),
                new Point(plot.Left, hitPointsLane.Bottom + LaneGap / 2), new Point(plot.Right, hitPointsLane.Bottom + LaneGap / 2));
            DrawLaneGrid(context, capacitorLane, capacitorMax, "GJ/s", 1);
        }

        if (series is null)
            return;

        using (context.PushClip(hitPointsLane.Inflate(new Thickness(0, 1, 0, 0))))
            foreach (var s in series)
                if (s.Lane is GraphLane.HitPoints)
                    DrawSeries(context, hitPointsLane, s, hitPointsMax, pxPerSample, visible);

        if (splits)
            using (context.PushClip(capacitorLane.Inflate(new Thickness(0, 1, 0, 0))))
                foreach (var s in series)
                    if (s.Lane is GraphLane.Capacitor)
                        DrawSeries(context, capacitorLane, s, capacitorMax, pxPerSample, visible);

        using (context.PushClip(plot))
            DrawMarkers(context, plot, pxPerSample);
    }

    private void DrawMarkers(DrawingContext context, Rect plot, double pxPerSample)
    {
        var markers = Markers;
        if (markers is null || markers.Count == 0)
            return;

        foreach (var marker in markers)
        {
            var x = plot.Right - marker.Age * pxPerSample;
            if (x < plot.Left)
                continue;
            // Short tick on the bottom axis — subtle, doesn't clutter the lines.
            context.DrawLine(new Pen(marker.Brush, 1.5), new Point(x, plot.Bottom), new Point(x, plot.Bottom - 7));
        }
    }

    // A lane's scale follows only the samples currently on screen, so an old spike that has scrolled past the left edge
    // no longer compresses the visible curve. Internal (not private) so a test can check the scale rule (ET-280) —
    // never lower than this — directly against a real series, without rendering pixels.
    internal static double ObservedMax(IReadOnlyList<DpsSeries>? series, GraphLane lane, int visible)
    {
        var max = 0.0;
        if (series is not null)
            foreach (var s in series)
                if (s.Lane == lane)
                    max = Math.Max(max, VisibleMax(s, visible));
        return max;
    }

    private static double VisibleMax(DpsSeries s, int visible)
    {
        var max = 0.0;
        var values = s.Values;
        for (var i = Math.Max(0, values.Count - visible); i < values.Count; i++)
            if (values[i] > max)
                max = values[i];
        return max;
    }

    // Vertical gridline + label every 5 seconds, anchored to "now" on the right, so the fixed time density is legible.
    private void DrawTimeGrid(DrawingContext context, Rect plot)
    {
        var pxPerSecond = PixelsPerSecond;
        if (pxPerSecond <= 0)
            return;

        const int intervalSeconds = 5;
        var pen = new Pen(GridBrush, 1);
        for (var seconds = intervalSeconds; ; seconds += intervalSeconds)
        {
            var x = plot.Right - seconds * pxPerSecond;
            if (x < plot.Left)
                break;
            context.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));

            var text = new FormattedText(
                FormatTime(seconds), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelTypeface, 10, LabelBrush);
            context.DrawText(text, new Point(x - text.Width / 2, plot.Bottom + 2));
        }
    }

    // Gridlines and tick labels for one lane, plus its unit in the lane's top-left corner so two stacked lanes can
    // never be read on each other's scale.
    private static void DrawLaneGrid(DrawingContext context, Rect lane, double max, string unit, int lines, bool labelsBaseline = true)
    {
        var pen = new Pen(GridBrush, 1);
        for (var i = 0; i <= lines; i++)
        {
            var fraction = i / (double)lines;
            var y = lane.Bottom - fraction * lane.Height;
            context.DrawLine(pen, new Point(lane.Left, y), new Point(lane.Right, y));
            if (i == 0 && !labelsBaseline)
                continue;

            var text = new FormattedText(
                FormatTick(max * fraction), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelTypeface, 10, LabelBrush);
            context.DrawText(text, new Point(lane.Left - text.Width - 6, y - text.Height / 2));
        }

        // In the gutter under the lane's top figure when the next figure is far enough down to leave room; otherwise
        // just inside the plot.
        var unitText = new FormattedText(unit, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelTypeface, 9, LabelBrush);
        context.DrawText(unitText, lane.Height / lines >= 26
            ? new Point(lane.Left - unitText.Width - 6, lane.Top + 7)
            : new Point(lane.Left + 4, lane.Top + 1));
    }

    private static void DrawSeries(DrawingContext context, Rect lane, DpsSeries s, double max, double pxPerSample, int visible)
    {
        var values = s.Values;
        // A line with nothing on screen is not drawn: eight flat lines stacked on the baseline would hide the one
        // that matters and say nothing themselves.
        if (values.Count < 2 || VisibleMax(s, visible) < 0.5)
            return;

        var start = Math.Max(0, values.Count - visible);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var first = true;
            for (var i = start; i < values.Count; i++)
            {
                var x = lane.Right - (values.Count - 1 - i) * pxPerSample;
                var y = lane.Bottom - Math.Clamp(values[i] / max, 0, 1) * lane.Height;
                var point = new Point(x, y);
                if (first)
                {
                    ctx.BeginFigure(point, isFilled: false);
                    first = false;
                }
                else
                    ctx.LineTo(point);
            }
            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(s.Stroke, 1.6, s.Dashed ? GivenDash : null, lineJoin: PenLineJoin.Round), geometry);
    }

    internal static double NiceCeiling(double value, double floor)
    {
        if (value <= floor)
            return floor;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var nice = normalized switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 2.5 => 2.5,
            <= 5 => 5,
            _ => 10
        };
        return nice * magnitude;
    }

    private static string FormatTick(double value) =>
        value >= 1000
            ? (value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k"
            : value.ToString("0", CultureInfo.InvariantCulture);

    private static string FormatTime(int seconds) =>
        seconds % 60 == 0
            ? (seconds / 60).ToString(CultureInfo.InvariantCulture) + "m"
            : seconds.ToString(CultureInfo.InvariantCulture) + "s";
}
