using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EveUtils.Client.Controls;

/// <summary>
/// A pilot as the app's hexagon: their portrait clipped to it, or their initial on the soft accent when there is no
/// portrait (no ESI link, images off, still loading) — the fallback every hex in the app already uses, so the three
/// read the same. One control for every size the runs screen draws (ET-290), where each screen used to inline its own
/// clip path.
///
/// Drawn rather than built from an Image and a TextBlock: a virtualised list recycles these by the hundred, and a
/// recycled container must never keep a previous row's portrait — it only ever draws what <see cref="Source"/> holds.
/// </summary>
public sealed class HexPortrait : Control
{
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<HexPortrait, double>(nameof(Size), 22);

    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<HexPortrait, IImage?>(nameof(Source));

    public static readonly StyledProperty<string?> InitialProperty =
        AvaloniaProperty.Register<HexPortrait, string?>(nameof(Initial));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<HexPortrait, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<HexPortrait, IBrush?>(nameof(Foreground));

    /// <summary>An outline around the hexagon: the hover and focus ring of a clickable one, or the gap that keeps two
    /// hexes in a stack apart. None when null.</summary>
    public static readonly StyledProperty<IBrush?> RingProperty =
        AvaloniaProperty.Register<HexPortrait, IBrush?>(nameof(Ring));

    public static readonly StyledProperty<double> RingThicknessProperty =
        AvaloniaProperty.Register<HexPortrait, double>(nameof(RingThickness), 1.5);

    static HexPortrait()
    {
        AffectsMeasure<HexPortrait>(SizeProperty);
        AffectsRender<HexPortrait>(SourceProperty, InitialProperty, FillProperty, ForegroundProperty, RingProperty,
            RingThicknessProperty);
    }

    /// <summary>The hexagon's width. Its height follows the app's own hex proportions (34 × 39).</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? Initial
    {
        get => GetValue(InitialProperty);
        set => SetValue(InitialProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? Ring
    {
        get => GetValue(RingProperty);
        set => SetValue(RingProperty, value);
    }

    public double RingThickness
    {
        get => GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, HeightFor(Size));

    public override void Render(DrawingContext context) =>
        Draw(context, default, Size, Source, Initial, Fill, Foreground, Ring, RingThickness);

    /// <summary>The hexagon's height for a width, in the app's own hex proportions (34 × 39).</summary>
    internal static double HeightFor(double width) => Math.Round(width * 39 / 34);

    /// <summary>One hexagon at <paramref name="origin"/> — shared with <see cref="HexStack"/>, which draws several.</summary>
    internal static void Draw(DrawingContext context, Point origin, double width, IImage? source, string? initial,
        IBrush? fill, IBrush? foreground, IBrush? ring, double ringThickness)
    {
        double height = HeightFor(width);
        Geometry hex = _Hex(origin, width, height);
        var bounds = new Rect(origin, new Size(width, height));

        using (context.PushGeometryClip(hex))
        {
            if (source is { Size: { Width: > 0, Height: > 0 } natural })
            {
                // Uniform to fill: the face stays centred and the hexagon never shows a letterbox.
                double scale = Math.Max(width / natural.Width, height / natural.Height);
                var shown = new Size(width / scale, height / scale);
                context.DrawImage(source,
                    new Rect((natural.Width - shown.Width) / 2, (natural.Height - shown.Height) / 2, shown.Width, shown.Height),
                    bounds);
            }
            else
            {
                context.DrawRectangle(fill, null, bounds);
                if (!string.IsNullOrEmpty(initial) && foreground is not null)
                {
                    var text = new FormattedText(initial, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), Math.Max(7, width * 0.42),
                        foreground);
                    context.DrawText(text, new Point(origin.X + (width - text.Width) / 2, origin.Y + (height - text.Height) / 2));
                }
            }
        }

        if (ring is not null && ringThickness > 0)
            context.DrawGeometry(null, new Pen(ring, ringThickness), hex);
    }

    private static StreamGeometry _Hex(Point origin, double width, double height)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext figure = geometry.Open();
        figure.BeginFigure(new Point(origin.X + width / 2, origin.Y), true);
        figure.LineTo(new Point(origin.X + width, origin.Y + height / 4));
        figure.LineTo(new Point(origin.X + width, origin.Y + height * 3 / 4));
        figure.LineTo(new Point(origin.X + width / 2, origin.Y + height));
        figure.LineTo(new Point(origin.X, origin.Y + height * 3 / 4));
        figure.LineTo(new Point(origin.X, origin.Y + height / 4));
        figure.EndFigure(true);
        return geometry;
    }
}
