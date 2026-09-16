using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace EveUtils.Client.Controls;

/// <summary>What a child of <see cref="RangeLinePanel"/> is on the runs overview's range line (ET-303).</summary>
public enum RangeLineSlot
{
    /// <summary>◀ title ✕ ▶ — always first, always left.</summary>
    Lead,

    /// <summary>The count and flown figures.</summary>
    Figures,

    /// <summary>The separator between <see cref="Figures"/> and <see cref="Isk"/>; left out whenever the two do not
    /// share a row, so no row ever starts or ends on a stray dot.</summary>
    Joint,

    /// <summary>The ISK bar and the net figure.</summary>
    Isk,

    /// <summary>PUBLISH n LOCAL and SUMMARY — always right-aligned.</summary>
    Trail
}

/// <summary>
/// The range line (ET-303): every piece gets its own rectangle at every width, and what does not fit moves to a row
/// of its own rather than into its neighbour. ET-302 put the totals and the actions in separate Grid columns, but the
/// month navigation still shared a column with the totals, and the narrow styles that were meant to move the totals
/// down never applied — a local <c>Grid.Row</c> outranks a style setter.
///
/// Measured, never guessed from a breakpoint: the figures change with the data ("1 activity" against "185 activities
/// · 27:46:11 flown · +2.35B ISK net"), so the arrangement is chosen from the pieces' own desired widths, widest first:
/// <list type="number">
/// <item>everything on one row, the totals right-aligned against the actions;</item>
/// <item>navigation and actions on the first row, the totals on the second;</item>
/// <item>navigation alone, then the actions right-aligned, then the totals.</item>
/// </list>
/// In both of the last two the totals split once more — figures on one row, ISK on the next — when even they are
/// wider than the line.
/// </summary>
public sealed class RangeLinePanel : Panel
{
    public static readonly AttachedProperty<RangeLineSlot> SlotProperty =
        AvaloniaProperty.RegisterAttached<RangeLinePanel, Control, RangeLineSlot>("Slot");

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<RangeLinePanel, double>(nameof(Gap), 16);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<RangeLinePanel, double>(nameof(Spacing), 10);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<RangeLinePanel, double>(nameof(RowSpacing), 6);

    static RangeLinePanel()
    {
        AffectsMeasure<RangeLinePanel>(GapProperty, SpacingProperty, RowSpacingProperty);
        AffectsParentMeasure<RangeLinePanel>(SlotProperty);
    }

    public static RangeLineSlot GetSlot(Control control) => control.GetValue(SlotProperty);

    public static void SetSlot(Control control, RangeLineSlot value) => control.SetValue(SlotProperty, value);

    /// <summary>The least room kept between the navigation, the totals and the actions.</summary>
    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>The room between the figures, the separator and the ISK within the totals.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (Control child in Children)
            child.Measure(Size.Infinity);

        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : _SingleRowWidth();
        List<Row> rows = _Plan(width);
        double height = rows.Sum(row => row.Height) + Math.Max(0, rows.Count - 1) * RowSpacing;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        List<Row> rows = _Plan(finalSize.Width);
        HashSet<Control> placed = [];
        double y = 0;
        foreach (Row row in rows)
        {
            foreach ((Control child, double x) in row.Items)
            {
                double childWidth = Math.Min(child.DesiredSize.Width, Math.Max(0, finalSize.Width - x));
                double childHeight = child.DesiredSize.Height;
                child.Arrange(new Rect(x, y + (row.Height - childHeight) / 2, childWidth, childHeight));
                placed.Add(child);
            }

            y += row.Height + RowSpacing;
        }

        // A Joint left out of every row, or a piece that is not visible: nothing may keep an old rectangle.
        foreach (Control child in Children)
            if (!placed.Contains(child))
                child.Arrange(default);

        return finalSize;
    }

    private double _Width(RangeLineSlot slot) => _Pieces(slot).Sum(child => child.DesiredSize.Width);

    private IEnumerable<Control> _Pieces(RangeLineSlot slot) =>
        Children.Where(child => child.IsVisible && GetSlot(child) == slot);

    private double _TotalsWidth() =>
        _Width(RangeLineSlot.Figures) + Spacing + _Width(RangeLineSlot.Joint) + Spacing + _Width(RangeLineSlot.Isk);

    private double _SingleRowWidth() =>
        _Width(RangeLineSlot.Lead) + Gap + _TotalsWidth() + Gap + _Width(RangeLineSlot.Trail);

    private List<Row> _Plan(double width)
    {
        double lead = _Width(RangeLineSlot.Lead);
        double trail = _Width(RangeLineSlot.Trail);
        double totals = _TotalsWidth();
        var rows = new List<Row>();

        if (_SingleRowWidth() <= width)
        {
            var row = new Row();
            row.Add(_Pieces(RangeLineSlot.Lead), 0);
            double trailStart = width - trail;
            row.Add(_Pieces(RangeLineSlot.Trail), trailStart);
            row.AddTotals(this, trailStart - Gap - totals);
            rows.Add(row);
            return rows;
        }

        var first = new Row();
        first.Add(_Pieces(RangeLineSlot.Lead), 0);
        rows.Add(first);
        if (lead + Gap + trail <= width)
        {
            first.Add(_Pieces(RangeLineSlot.Trail), width - trail);
        }
        else
        {
            var actions = new Row();
            actions.Add(_Pieces(RangeLineSlot.Trail), Math.Max(0, width - trail));
            rows.Add(actions);
        }

        if (totals <= width)
        {
            var totalsRow = new Row();
            totalsRow.AddTotals(this, 0);
            rows.Add(totalsRow);
        }
        else
        {
            var figures = new Row();
            figures.Add(_Pieces(RangeLineSlot.Figures), 0);
            var isk = new Row();
            isk.Add(_Pieces(RangeLineSlot.Isk), 0);
            rows.Add(figures);
            rows.Add(isk);
        }

        return rows.Where(row => row.Items.Count > 0).ToList();
    }

    private sealed class Row
    {
        public List<(Control Child, double X)> Items { get; } = [];

        public double Height => Items.Count == 0 ? 0 : Items.Max(item => item.Child.DesiredSize.Height);

        /// <summary>Pieces of one slot, left to right from <paramref name="x"/>, touching: the spacing inside a slot
        /// is the pieces' own margins.</summary>
        public double Add(IEnumerable<Control> pieces, double x)
        {
            foreach (Control piece in pieces)
            {
                Items.Add((piece, x));
                x += piece.DesiredSize.Width;
            }

            return x;
        }

        public void AddTotals(RangeLinePanel panel, double x)
        {
            x = Add(panel._Pieces(RangeLineSlot.Figures), x) + panel.Spacing;
            x = Add(panel._Pieces(RangeLineSlot.Joint), x) + panel.Spacing;
            Add(panel._Pieces(RangeLineSlot.Isk), x);
        }
    }
}
