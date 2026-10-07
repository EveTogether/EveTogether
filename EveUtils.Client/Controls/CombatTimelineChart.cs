using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.Controls;

/// <summary>
/// A stored run's combat over its whole length (ET-468): hp/s and GJ/s in two lanes with the fixed combat inks, idle
/// stretches hatched on the baseline, and the exact 5 s values of the bucket under the pointer.
/// </summary>
public sealed class CombatTimelineChart : Control
{
    public static readonly StyledProperty<CombatChartModel?> ModelProperty =
        AvaloniaProperty.Register<CombatTimelineChart, CombatChartModel?>(nameof(Model));

    private const double Left = 58, Right = 10, Top = 22, Bottom = 20, LaneGap = 14, CapacitorShare = 0.28;

    private static readonly CombatSeriesKind[] HitPointSeries =
        [CombatSeriesKind.RepIn, CombatSeriesKind.RepOut, CombatSeriesKind.DmgIn, CombatSeriesKind.DmgOut];

    private static readonly CombatSeriesKind[] CapacitorSeries =
        [CombatSeriesKind.CapIn, CombatSeriesKind.CapOut, CombatSeriesKind.NeutOut, CombatSeriesKind.NeutIn];

    private int? _hoverBucket;

    static CombatTimelineChart() => AffectsRender<CombatTimelineChart>(ModelProperty);

    public CombatChartModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public static IBrush InkOf(CombatSeriesKind kind) => kind switch
    {
        CombatSeriesKind.DmgOut => CombatInk.Out,
        CombatSeriesKind.DmgIn => CombatInk.In,
        CombatSeriesKind.RepOut or CombatSeriesKind.RepIn => CombatInk.Rep,
        CombatSeriesKind.NeutOut or CombatSeriesKind.NeutIn => CombatInk.Neut,
        _ => CombatInk.Cap
    };

    /// <summary>
    /// The way out is dashed where one ink stands for both directions of a lane.
    /// </summary>
    public static bool IsDashed(CombatSeriesKind kind) =>
        kind is CombatSeriesKind.RepOut or CombatSeriesKind.NeutOut or CombatSeriesKind.CapOut;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Model is not { } model)
        {
            return;
        }

        double width = Bounds.Width - Left - Right;
        double x = e.GetPosition(this).X - Left;
        int buckets = _BucketCount(model);
        _hoverBucket = x < 0 || x > width || width <= 0 ? null : Math.Min(buckets - 1, (int)(x / width * buckets));
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverBucket = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Model is not { } model)
        {
            return;
        }

        IBrush Brush(string key, IBrush fallback) =>
            this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : fallback;
        IBrush dim = Brush("TextDimBrush", Brushes.Gray);
        IBrush text = Brush("TextBrightBrush", Brushes.White);
        IBrush divider = Brush("DividerBrush", Brushes.DimGray);
        Typeface mono = this.TryFindResource("MonoFont", out object? font) && font is FontFamily family
            ? new Typeface(family)
            : Typeface.Default;

        double width = Bounds.Width - Left - Right, height = Bounds.Height - Top - Bottom;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double capacitorHeight = Math.Round((height - LaneGap) * CapacitorShare);
        Rect hitPoints = new(Left, Top, width, height - LaneGap - capacitorHeight);
        Rect capacitor = new(Left, hitPoints.Bottom + LaneGap, width, capacitorHeight);
        int buckets = _BucketCount(model);
        double X(double bucket) => Left + width * bucket / buckets;

        void Label(string value, double x, double y, IBrush brush, TextAlignment align)
        {
            FormattedText formatted = new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, mono, 9.5, brush);
            double dx = align switch { TextAlignment.Right => -formatted.Width, TextAlignment.Center => -formatted.Width / 2, _ => 0 };
            context.DrawText(formatted, new Point(x + dx, y - formatted.Height / 2));
        }

        Pen gridPen = new(divider, 1);
        _Lane(context, model, hitPoints, HitPointSeries, 100, 4, "HP/s", gridPen, X, Label, dim);
        _Lane(context, model, capacitor, CapacitorSeries, 10, 2, "GJ/s", gridPen, X, Label, dim);

        // A tick a minute, or every two once the run is longer than twenty.
        int step = model.Seconds > 1200 ? 120 : 60;
        for (int second = 0; second <= model.Seconds; second += step)
        {
            Label(TimeSpan.FromSeconds(second).ToString(@"mm\:ss"), X((double)second / model.BucketSeconds),
                capacitor.Bottom + 11, dim, TextAlignment.Center);
        }

        _Idle(context, model, hitPoints, dim, X);

        if (_hoverBucket is { } hover)
        {
            context.DrawLine(new Pen(dim, 1), new Point(X(hover + 0.5), hitPoints.Top), new Point(X(hover + 0.5), capacitor.Bottom));
            Label(_Readout(model, hover), Left + width, Top - 12, text, TextAlignment.Right);
        }
    }

    private static void _Lane(DrawingContext context, CombatChartModel model, Rect lane, CombatSeriesKind[] series,
        double floor, int steps, string unit, Pen gridPen, Func<double, double> x, Action<string, double, double, IBrush, TextAlignment> label,
        IBrush dim)
    {
        Dictionary<CombatSeriesKind, double[]> lines = series.Where(model.Buckets.ContainsKey)
            .ToDictionary(kind => kind, kind => _Smoothed(model.Buckets[kind], model.BucketSeconds));
        double max = _Ceiling(lines.Values.SelectMany(rates => rates).DefaultIfEmpty().Max(), floor);
        double Y(double rate) => lane.Bottom - lane.Height * Math.Min(rate, max) / max;

        foreach (double rate in Enumerable.Range(0, steps + 1).Select(step => max * step / steps))
        {
            context.DrawLine(gridPen, new Point(lane.Left, Y(rate)), new Point(lane.Right, Y(rate)));
            label(rate.ToString("0", CultureInfo.InvariantCulture), lane.Left - 6, Y(rate), dim, TextAlignment.Right);
        }
        label(unit, lane.Left - 56, Y(max * (2 * steps - 1) / (2 * steps)), dim, TextAlignment.Left);

        foreach ((CombatSeriesKind kind, double[] rates) in lines)
        {
            Pen pen = new(InkOf(kind), 1.5, IsDashed(kind) ? new DashStyle([4, 3], 0) : null);
            StreamGeometry line = new();
            using (StreamGeometryContext path = line.Open())
            {
                path.BeginFigure(new Point(x(0.5), Y(rates[0])), false);
                for (int bucket = 1; bucket < rates.Length; bucket++)
                {
                    path.LineTo(new Point(x(bucket + 0.5), Y(rates[bucket])));
                }
                path.EndFigure(false);
            }
            context.DrawGeometry(null, pen, line);
        }
    }

    // The light smoothing the drawn line gets (1-2-1 over neighbouring buckets); the readout keeps the exact values.
    private static double[] _Smoothed(long[] sums, int bucketSeconds) =>
    [
        .. sums.Select((_, bucket) => (sums[Math.Max(0, bucket - 1)] + 2 * sums[bucket]
            + sums[Math.Min(sums.Length - 1, bucket + 1)]) / (4.0 * bucketSeconds))
    ];

    // A top that quarters into round figures (150 · 300 · 450 · 600), closer to the peak than a decade step.
    private static double _Ceiling(double value, double floor)
    {
        if (value <= floor)
        {
            return floor;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        return new[] { 1, 1.2, 1.6, 2, 3, 4, 6, 8, 10 }.Select(step => step * magnitude).First(top => top >= value);
    }

    // Short diagonal strokes on the baseline under every idle stretch, the way the mockup hatches it.
    private static void _Idle(DrawingContext context, CombatChartModel model, Rect lane, IBrush dim, Func<double, double> x)
    {
        Pen hatch = new(dim, 1);
        for (int second = 0; second < model.Idle.Length; second += 2)
        {
            if (!model.Idle[second])
            {
                continue;
            }

            double left = x((double)second / model.BucketSeconds);
            context.DrawLine(hatch, new Point(left, lane.Bottom), new Point(left + 3, lane.Bottom - 5));
        }
    }

    private static string _Readout(CombatChartModel model, int bucket)
    {
        int from = bucket * model.BucketSeconds;
        IEnumerable<string> parts = model.Buckets
            .OrderBy(pair => pair.Key)
            .Select(pair => $"{_Short(pair.Key)} {(pair.Value[bucket] / (double)model.BucketSeconds).ToString("0.#", CultureInfo.InvariantCulture)}");
        return $"{TimeSpan.FromSeconds(from):mm\\:ss}–{TimeSpan.FromSeconds(from + model.BucketSeconds):mm\\:ss} · {string.Join(" · ", parts)}";
    }

    private static string _Short(CombatSeriesKind kind) => kind switch
    {
        CombatSeriesKind.DmgOut => "out",
        CombatSeriesKind.DmgIn => "in",
        CombatSeriesKind.RepOut => "reps out",
        CombatSeriesKind.RepIn => "reps in",
        CombatSeriesKind.NeutOut => "neut out GJ",
        CombatSeriesKind.NeutIn => "neut in GJ",
        CombatSeriesKind.CapOut => "cap out GJ",
        _ => "cap in GJ"
    };

    private static int _BucketCount(CombatChartModel model) =>
        Math.Max(1, (model.Seconds + model.BucketSeconds - 1) / model.BucketSeconds);
}
