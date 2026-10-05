using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EveUtils.Client.ViewModels.FitBrowser;

namespace EveUtils.Client.Views;

/// <summary>
/// SKILL IMPACT's "COMBINED GAIN … AGAINST TRAINING TIME" chart (mockup v5 "f · from a fit"): a step line of the
/// greedy curve, y 0/50/100 %, x in days, the optimal point filled with its label and the 100% point hollow.
/// </summary>
public sealed class SkillTargetCurveChart : Control
{
    public static readonly StyledProperty<SkillTargetCurveViewModel?> CurveProperty =
        AvaloniaProperty.Register<SkillTargetCurveChart, SkillTargetCurveViewModel?>(nameof(Curve));

    private const double Left = 44, Right = 16, Top = 12, Bottom = 22;

    static SkillTargetCurveChart() => AffectsRender<SkillTargetCurveChart>(CurveProperty);

    public SkillTargetCurveViewModel? Curve
    {
        get => GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Curve is not { } curve)
        {
            return;
        }

        IBrush Brush(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
        var accent = Brush("AccentBrightBrush", Brushes.SkyBlue);
        var dim = Brush("TextDimBrush", Brushes.Gray);
        var text = Brush("TextBrush", Brushes.White);
        var grid = Brush("DividerBrush", Brushes.DimGray);
        var panel = Brush("BgPanelBrush", Brushes.Black);
        var mono = this.TryFindResource("MonoFont", out var font) && font is FontFamily family ? new Typeface(family) : Typeface.Default;

        double width = Bounds.Width - Left - Right, height = Bounds.Height - Top - Bottom;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double totalHours = curve.Points.Count == 0 ? 24 : Math.Max(curve.Points[^1].CumulativeHours, 1);
        double axisHours = totalHours * 1.08;
        double X(double hours) => Left + width * hours / axisHours;
        double Y(double percent) => Top + height * (1 - percent / 100);

        void Label(string value, double x, double y, IBrush brush, Typeface typeface, double size, TextAlignment align)
        {
            var formatted = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush);
            double dx = align switch { TextAlignment.Right => -formatted.Width, TextAlignment.Center => -formatted.Width / 2, _ => 0 };
            context.DrawText(formatted, new Point(x + dx, y - formatted.Height / 2));
        }

        var gridPen = new Pen(grid, 1);
        foreach (var percent in new[] { 0, 50, 100 })
        {
            context.DrawLine(gridPen, new Point(Left, Y(percent)), new Point(Left + width, Y(percent)));
            Label($"{percent}%", Left - 6, Y(percent), dim, mono, 9.5, TextAlignment.Right);
        }

        // Ticks on a round step in days (hours below two days), at most five of them across the axis.
        bool inDays = axisHours >= 48;
        double unit = inDays ? 24 : 1;
        double span = axisHours / unit;
        double step = new double[] { 1, 2, 5, 10, 25, 50, 100, 200, 500, 1000, 2000, 5000 }
            .FirstOrDefault(candidate => span / candidate <= 5, Math.Ceiling(span / 5));
        for (double tick = 0; tick <= span; tick += step)
        {
            Label($"{tick:0}{(inDays ? "d" : "h")}", X(tick * unit), Top + height + 12, dim, mono, 9.5, TextAlignment.Center);
        }

        var line = new StreamGeometry();
        using (var geometry = line.Open())
        {
            double y = Y(0);
            geometry.BeginFigure(new Point(X(0), y), false);
            foreach (var point in curve.Points)
            {
                geometry.LineTo(new Point(X(point.CumulativeHours), y));
                y = Y(point.ScorePercent);
                geometry.LineTo(new Point(X(point.CumulativeHours), y));
            }
            geometry.LineTo(new Point(Left + width, y));
            geometry.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(accent, 2), line);

        var accentPen = new Pen(accent, 2);
        var (optimalX, optimalY) = curve.OptimalPointIndex < 0
            ? (X(0), Y(0))
            : (X(curve.Points[curve.OptimalPointIndex].CumulativeHours), Y(curve.Points[curve.OptimalPointIndex].ScorePercent));
        context.DrawEllipse(accent, new Pen(panel, 2), new Point(optimalX, optimalY), 5.5, 5.5);
        Label(curve.OptimalLabel, optimalX + 10, optimalY + 15, text, Typeface.Default, 11, TextAlignment.Left);

        if (curve.FullPointIndex >= 0)
        {
            var full = curve.Points[curve.FullPointIndex];
            double fullX = X(full.CumulativeHours), fullY = Y(full.ScorePercent);
            context.DrawEllipse(panel, accentPen, new Point(fullX, fullY), 4.5, 4.5);
            Label(curve.EndLabel, fullX - 10, fullY + 16, text, Typeface.Default, 11, TextAlignment.Right);
        }
    }
}
