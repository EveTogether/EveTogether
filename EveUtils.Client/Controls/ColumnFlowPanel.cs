using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace EveUtils.Client.Controls;

/// <summary>
/// A TYPES or CHARACTERS tile column (ET-293): fills top-to-bottom, column by column — item 0 under item 1 under
/// item 2 in column 0, the rest starting column 1 — rather than <see cref="WrapPanel"/>'s row-major order or
/// <see cref="FillGridPanel"/>'s, neither of which reads as "a column of tiles" once there is more than one column.
///
/// The column count is chosen once per pass from the tiles' own widest natural width, never fixed and never
/// <c>Auto</c> on the Grid column around it (ET-285): the greatest count up to <see cref="MaxColumns"/> whose column
/// width still fits that widest tile, falling back one column at a time and bottoming out at one. A name too wide
/// even for a single column is left to its own <c>TextBlock</c> to trim with a tooltip — this panel only ever hands
/// out the width, never clips.
///
/// Rows stretch to fill whatever height this panel is given, never shorter than <see cref="MinRowHeight"/>: the tile
/// rows reach the strip's own height beside them rather than leaving a gap under a short column (RO-4's "de hoogte
/// wat meer benutten").
/// </summary>
public sealed class ColumnFlowPanel : Panel
{
    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<ColumnFlowPanel, int>(nameof(MaxColumns), 2);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<ColumnFlowPanel, double>(nameof(ColumnSpacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<ColumnFlowPanel, double>(nameof(RowSpacing));

    public static readonly StyledProperty<double> MinRowHeightProperty =
        AvaloniaProperty.Register<ColumnFlowPanel, double>(nameof(MinRowHeight), 28);

    static ColumnFlowPanel()
    {
        AffectsMeasure<ColumnFlowPanel>(MaxColumnsProperty, ColumnSpacingProperty, RowSpacingProperty, MinRowHeightProperty);
    }

    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
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

    public double MinRowHeight
    {
        get => GetValue(MinRowHeightProperty);
        set => SetValue(MinRowHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
            return new Size(0, 0);

        // Pass 1: every tile's own natural width, unconstrained — what "the widest desired tile width" below is
        // chosen around, and (for a 37-character name) what pushes the block down to one column in the first place.
        foreach (Control child in Children)
            child.Measure(Size.Infinity);
        double widest = Children.Max(child => child.DesiredSize.Width);

        double availableWidth = double.IsFinite(availableSize.Width) ? availableSize.Width : widest;
        int columns = _ColumnsFor(availableWidth, widest);
        int rows = _RowsFor(columns);
        double scale = LayoutHelper.GetLayoutScale(this);

        // Pass 2: re-measure at the column's real width, so a trimmed name's own desired height (never more than one
        // line here) is what the row height below is measured from, not the unconstrained pass above.
        for (int index = 0; index < Children.Count; index++)
        {
            int column = index / rows;
            double columnWidth = FillGridGeometry.ColumnWidth(column, columns, availableWidth, 1, ColumnSpacing, UseLayoutRounding, scale);
            Children[index].Measure(new Size(columnWidth, double.PositiveInfinity));
        }

        double height = double.IsFinite(availableSize.Height)
            ? availableSize.Height
            : rows * MinRowHeight + (rows - 1) * RowSpacing;
        return new Size(availableWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
            return finalSize;

        double widest = Children.Max(child => child.DesiredSize.Width);
        int columns = _ColumnsFor(finalSize.Width, widest);
        int rows = _RowsFor(columns);
        double scale = LayoutHelper.GetLayoutScale(this);
        double rowHeight = Math.Max(MinRowHeight, (finalSize.Height - (rows - 1) * RowSpacing) / rows);

        for (int index = 0; index < Children.Count; index++)
        {
            int column = index / rows;
            int row = index % rows;
            double x = FillGridGeometry.Edge(column, columns, finalSize.Width, 1, ColumnSpacing, UseLayoutRounding, scale);
            double width = FillGridGeometry.ColumnWidth(column, columns, finalSize.Width, 1, ColumnSpacing, UseLayoutRounding, scale);
            Children[index].Arrange(new Rect(x, row * (rowHeight + RowSpacing), width, rowHeight));
        }

        return finalSize;
    }

    private int _RowsFor(int columns) => (int)Math.Ceiling(Children.Count / (double)columns);

    /// <summary>The greatest column count up to <see cref="MaxColumns"/> whose share of <paramref name="availableWidth"/>
    /// still fits <paramref name="widestDesired"/>; one column is always the floor, trimmed by the tile itself where
    /// even that is not enough (ET-285: this panel measures with what it was given, it never grows past it).</summary>
    private int _ColumnsFor(double availableWidth, double widestDesired)
    {
        for (int columns = Math.Max(1, MaxColumns); columns > 1; columns--)
        {
            double columnWidth = (availableWidth - (columns - 1) * ColumnSpacing) / columns;
            if (columnWidth >= widestDesired)
                return columns;
        }

        return 1;
    }
}
