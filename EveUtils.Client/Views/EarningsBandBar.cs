using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EveUtils.Client.Views;

/// <summary>
/// The range of an activity's runs with the typical run marked and, while a run is going, where its TOTAL ISK stands
/// (ET-516). Positions arrive as fractions of the bar's width, already on the scale the view model chose, so the bar
/// only draws; it holds no numbers of its own.
/// </summary>
public sealed class EarningsBandBar : Control
{
    public const double BarHeight = 30;
    private const double TrackTop = 12;
    private const double TrackHeight = 6;
    private const double RangeTop = 10;
    private const double RangeHeight = 10;
    private const double TypicalTop = 6;
    private const double TypicalHeight = 18;
    private const double MarkWidth = 2;
    private const double FlagWidth = 8;
    private const double FlagHeight = 7;

    public static readonly StyledProperty<double> RangeStartProperty = AvaloniaProperty.Register<EarningsBandBar, double>(nameof(RangeStart));
    public static readonly StyledProperty<double> RangeEndProperty = AvaloniaProperty.Register<EarningsBandBar, double>(nameof(RangeEnd));
    public static readonly StyledProperty<double> TypicalMarkProperty = AvaloniaProperty.Register<EarningsBandBar, double>(nameof(TypicalMark));
    public static readonly StyledProperty<double?> CurrentMarkProperty = AvaloniaProperty.Register<EarningsBandBar, double?>(nameof(CurrentMark));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<EarningsBandBar, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> RangeBrushProperty = AvaloniaProperty.Register<EarningsBandBar, IBrush?>(nameof(RangeBrush));
    public static readonly StyledProperty<IBrush?> TypicalBrushProperty = AvaloniaProperty.Register<EarningsBandBar, IBrush?>(nameof(TypicalBrush));
    public static readonly StyledProperty<IBrush?> CurrentBrushProperty = AvaloniaProperty.Register<EarningsBandBar, IBrush?>(nameof(CurrentBrush));

    static EarningsBandBar()
    {
        AffectsRender<EarningsBandBar>(RangeStartProperty, RangeEndProperty, TypicalMarkProperty, CurrentMarkProperty,
            TrackBrushProperty, RangeBrushProperty, TypicalBrushProperty, CurrentBrushProperty);
    }

    public double RangeStart { get => GetValue(RangeStartProperty); set => SetValue(RangeStartProperty, value); }
    public double RangeEnd { get => GetValue(RangeEndProperty); set => SetValue(RangeEndProperty, value); }
    public double TypicalMark { get => GetValue(TypicalMarkProperty); set => SetValue(TypicalMarkProperty, value); }
    public double? CurrentMark { get => GetValue(CurrentMarkProperty); set => SetValue(CurrentMarkProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? RangeBrush { get => GetValue(RangeBrushProperty); set => SetValue(RangeBrushProperty, value); }
    public IBrush? TypicalBrush { get => GetValue(TypicalBrushProperty); set => SetValue(TypicalBrushProperty, value); }
    public IBrush? CurrentBrush { get => GetValue(CurrentBrushProperty); set => SetValue(CurrentBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, BarHeight);

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        context.DrawRectangle(TrackBrush, null, new Rect(0, TrackTop, width, TrackHeight));

        double start = _X(RangeStart, width);
        context.DrawRectangle(RangeBrush, null, new Rect(start, RangeTop, Math.Max(MarkWidth, _X(RangeEnd, width) - start), RangeHeight));
        context.DrawRectangle(TypicalBrush, null, new Rect(_X(TypicalMark, width) - MarkWidth / 2, TypicalTop, MarkWidth, TypicalHeight));

        if (CurrentMark is not { } current)
            return;

        double x = _X(current, width);
        context.DrawRectangle(CurrentBrush, null, new Rect(x - MarkWidth / 2, FlagHeight, MarkWidth, BarHeight - FlagHeight));
        StreamGeometry flag = new();
        using (StreamGeometryContext path = flag.Open())
        {
            path.BeginFigure(new Point(x - FlagWidth / 2, 0), true);
            path.LineTo(new Point(x + FlagWidth / 2, 0));
            path.LineTo(new Point(x, FlagHeight));
            path.EndFigure(true);
        }

        context.DrawGeometry(CurrentBrush, null, flag);
    }

    private static double _X(double fraction, double width) => Math.Clamp(fraction, 0d, 1d) * width;
}
