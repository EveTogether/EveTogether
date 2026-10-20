using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EveUtils.Client.ViewModels.Runs;

namespace EveUtils.Client.Views;

/// <summary>
/// How an activity's runs spread out (ET-517): bars of how many runs land where, on a log scale because a site's runs
/// run from a few million to a jackpot hundreds of times that. The typical run and the band that holds most runs are
/// marked, the long-run average when there is one (dotted), and the pilot's own runs sit as dots on a row of their own.
/// Everything arrives as fractions of the width from the view model, which chose the scale: like
/// <see cref="EarningsBandBar"/> this only draws, and no chart library is involved.
/// </summary>
public sealed class EarningsHistogram : Control
{
    public const double ChartHeight = 100;
    private const double DotRowHeight = 14;
    private const double BarGap = 2;
    private const double DotRadius = 3.2;

    public static readonly StyledProperty<EarningsDistribution?> DistributionProperty =
        AvaloniaProperty.Register<EarningsHistogram, EarningsDistribution?>(nameof(Distribution));
    public static readonly StyledProperty<IBrush?> BarBrushProperty = AvaloniaProperty.Register<EarningsHistogram, IBrush?>(nameof(BarBrush));
    public static readonly StyledProperty<IBrush?> BandBrushProperty = AvaloniaProperty.Register<EarningsHistogram, IBrush?>(nameof(BandBrush));
    public static readonly StyledProperty<IBrush?> TypicalBrushProperty = AvaloniaProperty.Register<EarningsHistogram, IBrush?>(nameof(TypicalBrush));
    public static readonly StyledProperty<IBrush?> MeanBrushProperty = AvaloniaProperty.Register<EarningsHistogram, IBrush?>(nameof(MeanBrush));
    public static readonly StyledProperty<IBrush?> OwnBrushProperty = AvaloniaProperty.Register<EarningsHistogram, IBrush?>(nameof(OwnBrush));

    static EarningsHistogram()
    {
        AffectsRender<EarningsHistogram>(DistributionProperty, BarBrushProperty, BandBrushProperty, TypicalBrushProperty,
            MeanBrushProperty, OwnBrushProperty);
    }

    public EarningsDistribution? Distribution { get => GetValue(DistributionProperty); set => SetValue(DistributionProperty, value); }
    public IBrush? BarBrush { get => GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public IBrush? BandBrush { get => GetValue(BandBrushProperty); set => SetValue(BandBrushProperty, value); }
    public IBrush? TypicalBrush { get => GetValue(TypicalBrushProperty); set => SetValue(TypicalBrushProperty, value); }
    public IBrush? MeanBrush { get => GetValue(MeanBrushProperty); set => SetValue(MeanBrushProperty, value); }
    public IBrush? OwnBrush { get => GetValue(OwnBrushProperty); set => SetValue(OwnBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, ChartHeight);

    public override void Render(DrawingContext context)
    {
        if (Distribution is not { } shape || Bounds.Width <= 0)
            return;

        double width = Bounds.Width;
        double plotBottom = ChartHeight - DotRowHeight;
        double bandLeft = _X(shape.BandStart, width);
        context.DrawRectangle(BandBrush, null, new Rect(bandLeft, 0, Math.Max(1, _X(shape.BandEnd, width) - bandLeft), plotBottom));

        double barWidth = width / Math.Max(1, shape.Bars.Count);
        for (int index = 0; index < shape.Bars.Count; index++)
        {
            double height = Math.Clamp(shape.Bars[index], 0d, 1d) * (plotBottom - 4);
            if (height <= 0)
                continue;

            context.DrawRectangle(BarBrush, null, new Rect(index * barWidth + BarGap / 2, plotBottom - height, Math.Max(1, barWidth - BarGap), height));
        }

        var bandPen = new Pen(TypicalBrush, 1, new DashStyle([3, 3], 0));
        context.DrawLine(bandPen, new Point(bandLeft, 0), new Point(bandLeft, plotBottom));
        context.DrawLine(bandPen, new Point(_X(shape.BandEnd, width), 0), new Point(_X(shape.BandEnd, width), plotBottom));
        if (shape.MeanMark is { } mean)
            context.DrawLine(new Pen(MeanBrush, 1.5, new DashStyle([1, 2], 0)), new Point(_X(mean, width), 6), new Point(_X(mean, width), plotBottom));

        context.DrawLine(new Pen(TypicalBrush, 2), new Point(_X(shape.TypicalMark, width), 0), new Point(_X(shape.TypicalMark, width), plotBottom));
        foreach (double own in shape.OwnMarks)
            context.DrawEllipse(OwnBrush, null, new Point(_X(own, width), ChartHeight - DotRowHeight / 2), DotRadius, DotRadius);
    }

    private static double _X(double fraction, double width) => Math.Clamp(fraction, 0d, 1d) * width;
}
