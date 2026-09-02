using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace EveUtils.Client.Controls;

/// <summary>
/// <see cref="FillGridPanel"/> that only builds the cards you can see. Same grid, same arithmetic
/// (<see cref="FillGridGeometry"/>): as many columns of at least <see cref="MinItemWidth"/> as fit, the remainder
/// divided over them, no strip of whitespace on the right.
///
/// Why it exists (ET-116): a card is expensive to bring into being — measured at roughly 10 ms each on the fit
/// browser's card, of which the layout is nothing and the construction is a quarter; the rest is the container,
/// its styles, its bindings and the text being shaped for the first time. A plain <see cref="Panel"/> pays that for
/// every row of the page, so a page of 100 cost 530 ms to put up while about fifteen of them were on screen. This
/// panel realises the rows the viewport touches and one row either side, and hands the rest back to a recycle pool,
/// so what a page costs follows the size of the window rather than the size of the page.
///
/// <b>It assumes every row is the same height.</b> That is what lets it know how tall the whole grid is without
/// measuring items it never builds — and it is true of a card grid by construction, since the cards come from one
/// template with a fixed picture band. The height is taken from the tallest card actually realised, so a template
/// that did vary would still lay out; it would simply reserve the tallest card's height for every row.
/// </summary>
public sealed class VirtualizingFillGridPanel : VirtualizingPanel
{
    /// <summary>How many rows beyond the viewport are kept realised on each side, so a scroll of less than a row
    /// does not have to build anything before it can draw.</summary>
    private const int CacheRows = 1;

    /// <inheritdoc cref="FillGridPanel.MinItemWidth"/>
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<VirtualizingFillGridPanel, double>(nameof(MinItemWidth), 1);

    /// <inheritdoc cref="FillGridPanel.ColumnSpacing"/>
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<VirtualizingFillGridPanel, double>(nameof(ColumnSpacing));

    /// <inheritdoc cref="FillGridPanel.RowSpacing"/>
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<VirtualizingFillGridPanel, double>(nameof(RowSpacing));

    static VirtualizingFillGridPanel()
    {
        AffectsMeasure<VirtualizingFillGridPanel>(MinItemWidthProperty, ColumnSpacingProperty, RowSpacingProperty);
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

    /// <summary>The containers standing in the grid right now, by item index.</summary>
    private readonly Dictionary<int, Control> _realized = [];

    /// <summary>Containers that have fallen out of view, by the generator's recycle key. They stay children of the
    /// panel, hidden: keeping the visual saves building it again, which is the whole point of the exercise.</summary>
    private readonly Dictionary<object, Stack<Control>> _pool = [];

    private readonly Dictionary<Control, object?> _recycleKeys = [];

    private Rect _viewport;
    private bool _hasViewport;
    private double _rowHeight;
    private int _columns = 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        if (items.Count == 0)
        {
            RecycleAll();
            return default;
        }

        double scale = LayoutHelper.GetLayoutScale(this);
        int columns = FillGridGeometry.ColumnCount(availableSize.Width, MinItemWidth, ColumnSpacing, items.Count);

        // Every item moves to a different slot when the column count changes, so nothing realised is in the right
        // place any more — cheaper to hand them all back to the pool than to shuffle them.
        if (columns != _columns)
        {
            RecycleAll();
            _columns = columns;
        }

        int rows = (items.Count + columns - 1) / columns;

        // A grid whose row height is not known yet cannot say which rows the viewport touches. Realise the first
        // item and let it answer the question; every row is that tall.
        if (_rowHeight <= 0)
        {
            var first = Realize(0);
            first.Measure(new Size(ColumnWidth(0, columns, availableSize.Width, scale), double.PositiveInfinity));
            _rowHeight = Math.Max(1, first.DesiredSize.Height);
        }

        double pitch = _rowHeight + RowSpacing;
        Rect viewport = _hasViewport ? _viewport : EstimatedViewport(availableSize);

        int firstRow = Math.Clamp((int)Math.Floor(viewport.Top / pitch) - CacheRows, 0, rows - 1);
        int lastRow = Math.Clamp((int)Math.Ceiling(viewport.Bottom / pitch) - 1 + CacheRows, firstRow, rows - 1);

        int from = firstRow * columns;
        int to = Math.Min(items.Count - 1, (lastRow + 1) * columns - 1);

        foreach (var index in _realized.Keys.Where(i => i < from || i > to).ToList())
            Recycle(index);

        double tallest = 0;
        for (var index = from; index <= to; index++)
        {
            var child = Realize(index);
            child.Measure(new Size(ColumnWidth(index % columns, columns, availableSize.Width, scale),
                double.PositiveInfinity));
            tallest = Math.Max(tallest, child.DesiredSize.Height);
        }

        // A taller card than anything realised so far changes how tall the whole grid is, so the extent below grows
        // with it and the next pass settles on the new pitch.
        if (tallest > _rowHeight) _rowHeight = tallest;

        // Report back exactly the width that was offered rather than the columns re-added: a hair over the offer is
        // enough for a host to fit a scrollbar it did not need.
        double width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : columns * Math.Max(1, MinItemWidth) + (columns - 1) * ColumnSpacing;

        return new Size(width, rows * _rowHeight + (rows - 1) * RowSpacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var items = Items;
        double scale = LayoutHelper.GetLayoutScale(this);
        int columns = FillGridGeometry.ColumnCount(finalSize.Width, MinItemWidth, ColumnSpacing, items.Count);
        double pitch = _rowHeight + RowSpacing;

        foreach (var (index, child) in _realized)
        {
            int column = index % columns;
            child.Arrange(new Rect(
                Edge(column, columns, finalSize.Width, scale),
                index / columns * pitch,
                ColumnWidth(column, columns, finalSize.Width, scale),
                child.DesiredSize.Height));
        }

        int rows = items.Count == 0 ? 0 : (items.Count + columns - 1) / columns;
        double extent = rows == 0 ? 0 : rows * _rowHeight + (rows - 1) * RowSpacing;

        // Take the WHOLE rect that was offered, not just the part the rows fill: Avalonia's ArrangeCore treats
        // VerticalAlignment.Stretch on the same branch as Center, so a panel that hands back less than it was given
        // is parked halfway down its viewport (ET-108).
        return new Size(finalSize.Width, Math.Max(finalSize.Height, extent));
    }

    // ── which part of the grid is on screen ──────────────────────────────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        _hasViewport = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var viewport = e.EffectiveViewport;
        bool changed = !_hasViewport || viewport != _viewport;
        _viewport = viewport;
        _hasViewport = true;
        if (changed) InvalidateMeasure();
    }

    /// <summary>What to assume before the first layout pass has told us where the viewport is. The window's own
    /// height is the honest guess — it is an upper bound on what any scroller inside it can show — and the real
    /// viewport arrives on the very next pass, which trims whatever this over-realised.</summary>
    private Rect EstimatedViewport(Size availableSize)
    {
        double height = double.IsFinite(availableSize.Height)
            ? availableSize.Height
            : VisualRoot?.Bounds.Height ?? 0;

        return new Rect(0, 0, availableSize.Width, height > 0 ? height : _rowHeight * 3);
    }

    // ── realising, recycling ─────────────────────────────────────────────────────────────────────────────────

    private Control Realize(int index)
    {
        if (_realized.TryGetValue(index, out var standing)) return standing;

        var generator = ItemContainerGenerator!;
        var item = Items[index];
        Control container;

        if (generator.NeedsContainer(item, index, out var recycleKey))
        {
            if (recycleKey is not null && _pool.TryGetValue(recycleKey, out var pool) && pool.Count > 0)
            {
                container = pool.Pop();
                container.IsVisible = true;
                generator.PrepareItemContainer(container, item, index);
                generator.ItemContainerPrepared(container, item, index);
            }
            else
            {
                container = generator.CreateContainer(item, index, recycleKey);
                AddInternalChild(container);
                generator.PrepareItemContainer(container, item, index);
                generator.ItemContainerPrepared(container, item, index);
            }

            _recycleKeys[container] = recycleKey;
        }
        else
        {
            // The item is its own container (a Control put straight in the collection) — it can never be recycled
            // into another item's place.
            container = (Control)item!;
            if (!Children.Contains(container)) AddInternalChild(container);
            container.IsVisible = true;
            generator.PrepareItemContainer(container, item, index);
            generator.ItemContainerPrepared(container, item, index);
            _recycleKeys[container] = null;
        }

        _realized[index] = container;
        return container;
    }

    private void Recycle(int index)
    {
        if (!_realized.Remove(index, out var container)) return;

        if (_recycleKeys.TryGetValue(container, out var recycleKey) && recycleKey is not null)
        {
            // NOT cleared on the way into the pool, and that is the whole difference between a pool that pays for
            // itself and one that does not. ItemsControl.ClearContainerForItemOverride empties the presenter's
            // Content, and a presenter with no content throws its child away — so the card the pool was holding on
            // to would be built again from the template on the way out. Measured: clearing here left 0 of 20 card
            // visuals reused across a page turn and the turn still cost ~230 ms; leaving the content standing lets
            // the presenter recycle its child through IRecyclingDataTemplate and only swap the data.
            // The price is that a pooled container keeps its old row alive until it is used again. That is bounded
            // by the pool — a screenful — and those rows are the library's own, which outlives the page anyway.
            container.IsVisible = false;
            if (!_pool.TryGetValue(recycleKey, out var pool)) _pool[recycleKey] = pool = new Stack<Control>();
            pool.Push(container);
        }
        else
        {
            ItemContainerGenerator!.ClearItemContainer(container);
            _recycleKeys.Remove(container);
            RemoveInternalChild(container);
        }
    }

    private void RecycleAll()
    {
        foreach (var index in _realized.Keys.ToList()) Recycle(index);
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        // Everything standing is against the old list, so it all goes back to the pool — from which the next measure
        // takes it straight out again. That is the point: a page turn reuses the visuals instead of building them.
        RecycleAll();
        InvalidateMeasure();
    }

    // ── what the ItemsControl asks of a virtualising panel ───────────────────────────────────────────────────

    protected override Control? ContainerFromIndex(int index) => _realized.GetValueOrDefault(index);

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, realized) in _realized)
            if (ReferenceEquals(realized, container)) return index;
        return -1;
    }

    protected override IEnumerable<Control> GetRealizedContainers() => _realized.Values;

    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count) return null;

        if (ContainerFromIndex(index) is null)
        {
            // Not realised, so there is nothing to bring into view yet: scroll to where the row will be, run the
            // pass that realises it, and pick it up on the other side.
            double pitch = _rowHeight + RowSpacing;
            this.BringIntoView(new Rect(0, index / Math.Max(1, _columns) * pitch, Bounds.Width, _rowHeight));
            UpdateLayout();
        }

        var container = ContainerFromIndex(index);
        container?.BringIntoView();
        return container;
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        int count = Items.Count;
        if (count == 0) return null;

        int current = from is Control control ? IndexFromContainer(control) : -1;
        int columns = Math.Max(1, _columns);

        int target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Next or NavigationDirection.Right => current + 1,
            NavigationDirection.Previous or NavigationDirection.Left => current - 1,
            NavigationDirection.Down => current + columns,
            NavigationDirection.Up => current - columns,
            NavigationDirection.PageDown => current + columns * RowsPerPage(),
            NavigationDirection.PageUp => current - columns * RowsPerPage(),
            _ => -1
        };

        if (target < 0 || target >= count)
        {
            if (!wrap || current < 0) return null;
            target = target < 0 ? count - 1 : 0;
        }

        return ScrollIntoView(target);
    }

    private int RowsPerPage() =>
        Math.Max(1, (int)Math.Floor((_hasViewport ? _viewport.Height : Bounds.Height) / (_rowHeight + RowSpacing)));

    private double Edge(int index, int columns, double available, double scale) =>
        FillGridGeometry.Edge(index, columns, available, MinItemWidth, ColumnSpacing, UseLayoutRounding, scale);

    private double ColumnWidth(int index, int columns, double available, double scale) =>
        FillGridGeometry.ColumnWidth(index, columns, available, MinItemWidth, ColumnSpacing, UseLayoutRounding, scale);
}
