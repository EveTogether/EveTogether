using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace EveUtils.Client.Controls;

/// <summary>
/// A card grid that fills the width it is given: <see cref="MinItemWidth"/> is a minimum, not a size. It fits as
/// many columns of at least that width as the available width allows and then divides that width equally over them,
/// so nothing is left as a strip of whitespace on the right. This is CSS's
/// <c>repeat(auto-fill, minmax(&lt;min&gt;, 1fr))</c>, which Avalonia has no panel for: <see cref="WrapPanel"/>
/// keeps children at their own desired width and leaves the remainder empty, and <see cref="Primitives.UniformGrid"/>
/// does divide equally but takes its column count from the item count or a fixed number rather than from the
/// available width, and knows no minimum — eight members in a narrow host become eight unreadable columns.
///
/// Empty columns are kept rather than collapsed (CSS <c>auto-fill</c>, not <c>auto-fit</c>): one card in a wide host
/// stays one card wide instead of stretching across the row.
///
/// Spacing lives here rather than in the item's own <c>Margin</c> so the panel accounts for the same gaps it lays
/// out — a card template carries no layout arithmetic, and the next screen to use this panel has nothing to copy.
/// </summary>
public sealed class FillGridPanel : Panel
{
    /// <summary>The narrowest a column may be. Columns are never narrower than this; they are wider whenever the
    /// leftover width divided over the columns that do fit makes them so.</summary>
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<FillGridPanel, double>(nameof(MinItemWidth), 1);

    /// <summary>Gap between two columns. Counted in the column arithmetic, so <c>n</c> columns leave <c>n-1</c> gaps
    /// and the outer edges stay flush with the panel.</summary>
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<FillGridPanel, double>(nameof(ColumnSpacing));

    /// <summary>Gap between two rows. Only between rows: no gap above the first or below the last.</summary>
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<FillGridPanel, double>(nameof(RowSpacing));

    static FillGridPanel()
    {
        AffectsMeasure<FillGridPanel>(MinItemWidthProperty, ColumnSpacingProperty, RowSpacingProperty);
    }

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int columns = ColumnCount(availableSize.Width);
        double scale = LayoutHelper.GetLayoutScale(this);

        double height = 0;
        double rowHeight = 0;

        for (var i = 0; i < Children.Count; i++)
        {
            Control child = Children[i];
            int column = i % columns;

            // Height is left unconstrained even when the host offers one: a row is as tall as the tallest card in
            // it, and handing a card the whole remaining height would let a stretching child swallow the rows below.
            child.Measure(new Size(ColumnWidth(column, columns, availableSize.Width, scale), double.PositiveInfinity));

            if (column == 0)
                height += (i == 0 ? 0 : RowSpacing) + rowHeight;
            rowHeight = column == 0 ? child.DesiredSize.Height : Math.Max(rowHeight, child.DesiredSize.Height);
        }

        // Report back exactly the width that was offered rather than the columns re-added: a hair over the offer is
        // enough for a host to fit a scrollbar it did not need.
        double width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : columns * Math.Max(1, MinItemWidth) + (columns - 1) * ColumnSpacing;

        return new Size(width, height + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = ColumnCount(finalSize.Width);
        double scale = LayoutHelper.GetLayoutScale(this);

        double top = 0;
        double rowHeight = 0;

        for (var i = 0; i < Children.Count; i++)
        {
            Control child = Children[i];
            int column = i % columns;
            if (column == 0 && i > 0)
            {
                top += rowHeight + RowSpacing;
                rowHeight = 0;
            }

            // The arrange slot is what gives the card its width: it has no Width of its own, and a Stretch alignment
            // alone would only fill whatever the card asked for.
            child.Arrange(new Rect(
                Edge(column, columns, finalSize.Width, scale),
                top,
                ColumnWidth(column, columns, finalSize.Width, scale),
                child.DesiredSize.Height));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }

        return new Size(finalSize.Width, top + rowHeight);
    }

    /// <summary>How many columns of at least <see cref="MinItemWidth"/> fit in <paramref name="available"/>. Never
    /// fewer than one: below the minimum the single card takes the full width rather than being clipped to a size
    /// nothing was designed for. Empty columns are counted (CSS <c>auto-fill</c>), so a lone card in a wide host
    /// stays one column wide.</summary>
    private int ColumnCount(double available)
    {
        if (!double.IsFinite(available))
            return Math.Max(1, Children.Count);

        double min = Math.Max(1, MinItemWidth);
        return Math.Max(1, (int)Math.Floor((available + ColumnSpacing) / (min + ColumnSpacing)));
    }

    /// <summary>The left edge of a column, snapped to whole device pixels. Columns are placed by their EDGES rather
    /// than by repeatedly adding a fractional width: an equal share is rarely a whole pixel, and a width rounded
    /// once per column and then added up drifts — enough to push the last column past the panel it is filling, which
    /// is the whitespace strip back again in mirror image. Snapping the edges instead keeps every boundary crisp,
    /// spreads the leftover fraction over the columns (never more than a pixel apart) and lands the last column
    /// exactly on the panel's right edge.</summary>
    private double Edge(int index, int columns, double available, double scale)
    {
        if (index >= columns)
            return available;

        if (!double.IsFinite(available))
            return index * (Math.Max(1, MinItemWidth) + ColumnSpacing);

        double edge = index * (available + ColumnSpacing) / columns;
        return UseLayoutRounding ? LayoutHelper.RoundLayoutValue(edge, scale) : edge;
    }

    /// <summary>A column's width: up to the next column's edge, minus the gap that goes between them. The last
    /// column runs to the panel's edge, so no gap is taken off it.</summary>
    private double ColumnWidth(int index, int columns, double available, double scale)
    {
        if (!double.IsFinite(available))
            return Math.Max(1, MinItemWidth);

        double gap = index == columns - 1 ? 0 : ColumnSpacing;
        return Math.Max(0, Edge(index + 1, columns, available, scale) - gap - Edge(index, columns, available, scale));
    }
}
