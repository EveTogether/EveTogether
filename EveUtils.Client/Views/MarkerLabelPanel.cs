using Avalonia;
using Avalonia.Controls;

namespace EveUtils.Client.Views;

/// <summary>
/// Holds one label centred over a position on the bar below it (ET-524), kept inside the panel's width so a marker at
/// either end of the bar does not push its label off the edge. The position is a fraction of the width, like the marks
/// <see cref="EarningsBandBar"/> draws.
/// </summary>
public sealed class MarkerLabelPanel : Panel
{
    public static readonly StyledProperty<double?> MarkProperty = AvaloniaProperty.Register<MarkerLabelPanel, double?>(nameof(Mark));

    static MarkerLabelPanel()
    {
        AffectsArrange<MarkerLabelPanel>(MarkProperty);
    }

    public double? Mark { get => GetValue(MarkProperty); set => SetValue(MarkProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double height = 0;
        foreach (Control child in Children)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (Control child in Children)
        {
            double width = Math.Min(child.DesiredSize.Width, finalSize.Width);
            double centre = Math.Clamp(Mark ?? 0, 0d, 1d) * finalSize.Width;
            double left = Math.Clamp(centre - width / 2, 0, Math.Max(0, finalSize.Width - width));
            child.Arrange(new Rect(left, 0, width, child.DesiredSize.Height));
        }

        return finalSize;
    }
}
