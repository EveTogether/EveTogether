using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace EveUtils.Client.Controls;

/// <summary>
/// Chips on one line, never two (ET-290): what does not fit is left out and a trailing ellipsis says so, the way a
/// trimmed text would. A list row keeps its fixed height this way — a chip strip that wraps is a row that grows, and a
/// virtualised list cannot have rows that change height under it. Where every chip has to be read, a screen shows
/// them in full (the activity pane, RO-2).
/// </summary>
public sealed class ChipLinePanel : Panel
{
    private const double EllipsisGap = 2;

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ChipLinePanel, double>(nameof(Spacing), 4);

    /// <summary>Drawn by the panel itself, beside the items rather than among them: the items control owns
    /// <see cref="Panel.Children"/>.</summary>
    private readonly TextBlock _ellipsis = new() { Text = "…", FontSize = 10, IsHitTestVisible = false, Opacity = 0 };

    static ChipLinePanel()
    {
        AffectsMeasure<ChipLinePanel>(SpacingProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ChipLinePanel>(true);
    }

    public ChipLinePanel() => VisualChildren.Add(_ellipsis);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Out of the way while the panel files the items' visuals by their index, then back after the last of them.
        VisualChildren.Remove(_ellipsis);
        base.ChildrenChanged(sender, e);
        VisualChildren.Add(_ellipsis);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (Control child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        _ellipsis.Measure(Size.Infinity);
        return new Size(Math.Min(_NeededWidth(), availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double ellipsisWidth = _ellipsis.DesiredSize.Width + EllipsisGap;
        double limit = _NeededWidth() > finalSize.Width ? finalSize.Width - ellipsisWidth : finalSize.Width;
        double x = 0;
        double? cutAt = null;
        foreach (Control child in Children)
        {
            double width = child.DesiredSize.Width;
            if (cutAt is null && x + width <= limit)
            {
                child.Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width + Spacing;
                continue;
            }

            // Out of the way at the right edge with no size at all, clipped, rather than hidden: toggling IsVisible
            // from inside a layout pass would ask for another one.
            cutAt ??= x;
            child.Arrange(new Rect(finalSize.Width, 0, 0, 0));
        }

        _ellipsis.Foreground = TextElement.GetForeground(this);
        _ellipsis.Opacity = cutAt is null ? 0 : 1;
        _ellipsis.Arrange(new Rect(cutAt ?? 0, (finalSize.Height - _ellipsis.DesiredSize.Height) / 2,
            _ellipsis.DesiredSize.Width, _ellipsis.DesiredSize.Height));
        return finalSize;
    }

    private double _NeededWidth()
    {
        Control[] children = [.. Children];
        return children.Sum(child => child.DesiredSize.Width) + Math.Max(0, children.Length - 1) * Spacing;
    }
}
