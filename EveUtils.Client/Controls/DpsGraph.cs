using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EveUtils.Client.Controls;

/// <summary>
/// Lightweight real-time line graph (PyEveLiveDPS style). Renders one polyline per series at a fixed time density —
/// a sample is always the same number of pixels wide (<see cref="PixelsPerSecond"/>), so a wider graph shows a longer
/// timeline instead of stretching the same window. The newest sample sits on the right ("now") and the curve scrolls
/// in from there; the Y axis auto-scales to the visible window. The owner mutates each series' values in place and
/// bumps <see cref="Revision"/> to trigger a redraw. Folded from the EVE-Utils demo (own code).
/// </summary>
public sealed class DpsGraph : Control
{
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#14FFFFFF"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#FF8A7E6B"));
    private static readonly Typeface LabelTypeface = new("Consolas");

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

    static DpsGraph()
    {
        AffectsRender<DpsGraph>(SeriesProperty, RevisionProperty, PixelsPerSecondProperty, SecondsPerSampleProperty, MarkersProperty);
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
        var max = NiceCeiling(ObservedMax(series, visible));
        DrawGrid(context, plot, max);
        DrawTimeGrid(context, plot);

        if (series is null)
            return;

        using (context.PushClip(plot))
        {
            foreach (var s in series)
                DrawSeries(context, plot, s, max, pxPerSample, visible);

            DrawMarkers(context, plot, pxPerSample);
        }
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

    // Y scale follows only the samples currently on screen, so an old spike that has scrolled past the left edge no
    // longer compresses the visible curve.
    private static double ObservedMax(IReadOnlyList<DpsSeries>? series, int visible)
    {
        var max = 0.0;
        if (series is not null)
            foreach (var s in series)
            {
                var values = s.Values;
                for (var i = Math.Max(0, values.Count - visible); i < values.Count; i++)
                    if (values[i] > max)
                        max = values[i];
            }
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

    private void DrawGrid(DrawingContext context, Rect plot, double max)
    {
        var pen = new Pen(GridBrush, 1);
        const int lines = 4;
        for (var i = 0; i <= lines; i++)
        {
            var fraction = i / (double)lines;
            var y = plot.Bottom - fraction * plot.Height;
            context.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));

            var value = max * fraction;
            var text = new FormattedText(
                FormatTick(value), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelTypeface, 10, LabelBrush);
            context.DrawText(text, new Point(plot.Left - text.Width - 6, y - text.Height / 2));
        }
    }

    private static void DrawSeries(DrawingContext context, Rect plot, DpsSeries s, double max, double pxPerSample, int visible)
    {
        var values = s.Values;
        if (values.Count < 2)
            return;

        var start = Math.Max(0, values.Count - visible);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var first = true;
            for (var i = start; i < values.Count; i++)
            {
                var x = plot.Right - (values.Count - 1 - i) * pxPerSample;
                var y = plot.Bottom - Math.Clamp(values[i] / max, 0, 1) * plot.Height;
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

        context.DrawGeometry(null, new Pen(s.Stroke, 1.6, lineJoin: PenLineJoin.Round), geometry);
    }

    private static double NiceCeiling(double value)
    {
        if (value <= 100)
            return 100;

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
