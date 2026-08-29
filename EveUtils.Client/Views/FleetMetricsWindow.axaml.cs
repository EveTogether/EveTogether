using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// Free-standing fleet-metrics window: one live DPS graph per active member plus the fleet roll-up
/// totals. Non-modal so its graphs keep updating beside the main + fleets windows; disposes its view-model (drops
/// the bus subscription) on close.
/// </summary>
/// <remarks>
/// Reordering (ET-28) is a ghost-and-marker drag, not a live re-sort. While you hold a member the list stands
/// completely still: a ghost of the row follows the cursor, the row it came from stays put and faded, and a marker
/// shows the one place it would land. The collection changes once, on drop. The first cut re-sorted on every pointer
/// move and read as the list rearranging itself for no visible reason — quiet turned out to matter more than
/// immediacy. It also makes cancelling free: nothing has moved yet, so Escape just puts the ghost away.
/// </remarks>
public partial class FleetMetricsWindow : ChromedWindow
{
    // Far enough that a click on a member's pop-out button, or a shaky press, is not read as the start of a drag.
    private const double DragThreshold = 4;
    private const double MarkerThickness = 3;

    // How far the ghost trails behind the cursor, so the cursor and the drop marker stay clear of it.
    private const double GhostLead = 18;

    private readonly ItemsControl? _members;
    private readonly Canvas? _dragLayer;
    private readonly Border? _ghost;
    private readonly ContentControl? _ghostContent;
    private readonly Border? _marker;
    private TopLevel? _keyHost;

    private DpsViewModel? _dragging;
    private Point _dragOrigin;
    private Point _grabOffset;
    private int _insertionIndex;
    private bool _dragStarted;

    public FleetMetricsWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Wired once, on the one ItemsControl, rather than per item template: list, grid and compact then drag
        // identically and a fourth density would need no gesture code of its own. The handlers travel with the
        // control, so they keep working when the module host reparents this content into a docked tab.
        _members = this.FindControl<ItemsControl>("MemberList");
        _dragLayer = this.FindControl<Canvas>("DragLayer");
        _ghost = this.FindControl<Border>("DragGhost");
        _ghostContent = this.FindControl<ContentControl>("DragGhostContent");
        _marker = this.FindControl<Border>("DropMarker");

        if (_members is null)
            return;

        _members.PointerPressed += OnMemberPointerPressed;
        _members.PointerMoved += OnMemberPointerMoved;
        _members.PointerReleased += OnMemberPointerReleased;
        _members.PointerCaptureLost += OnMemberPointerCaptureLost;

        // Same reasoning as the drag handlers: wired once on the one ItemsControl, so all three densities show the
        // same member menu and it survives the module host reparenting this content into a docked tab. Tunnelling, so
        // the menu's lines are rebuilt with the current facts before the popup is put together.
        _members.AddHandler(ContextRequestedEvent, OnMemberContextRequested, RoutingStrategies.Tunnel);
    }

    // A relative "last update" line is only true at the moment it is read, so the menu is rebuilt on each request
    // rather than kept up to date by 40 rows re-rendering a clock.
    private void OnMemberContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_members is null || ViewModel is not { } viewModel)
            return;

        if (e.Source is Visual source && FindMember(source) is { } tracker)
            viewModel.RefreshMemberMenu(tracker);
    }

    // The member behind whatever was right-clicked: keyboard-invoked menus and clicks on inner controls both start
    // deeper in the tree than the row itself, so walk up to the first element carrying a member.
    private static DpsViewModel? FindMember(Visual source)
    {
        for (Visual? visual = source; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control { DataContext: DpsViewModel tracker })
                return tracker;

        return null;
    }

    public FleetMetricsWindow(FleetMetricsViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    private FleetMetricsViewModel? ViewModel => DataContext as FleetMetricsViewModel;

    // Grid lays its cards out across the row, so its drop marker stands between two columns; the stacked layouts
    // want it between two rows. Read off the panel that is actually there rather than off the layout enum.
    private bool DragsHorizontally => _members?.ItemsPanelRoot is WrapPanel;

    private void OnMemberPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_members is null || !e.GetCurrentPoint(_members).Properties.IsLeftButtonPressed)
            return;

        // A button inside the row (the pop-out) marks the press handled, so this never sees it.
        _dragOrigin = e.GetPosition(_members);
        _dragging = ContainerAt(_dragOrigin)?.DataContext as DpsViewModel;
        _dragStarted = false;
    }

    private void OnMemberPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is null || _members is null)
            return;

        Point position = e.GetPosition(_members);
        if (!_dragStarted && Point.Distance(position, _dragOrigin) >= DragThreshold)
            BeginDrag(e, position);

        if (_dragStarted)
            TrackDrag(position);
    }

    private void BeginDrag(PointerEventArgs e, Point position)
    {
        if (_dragging is null || _members is null || _ghost is null || _ghostContent is null)
            return;

        _dragStarted = true;
        e.Pointer.Capture(_members);

        // Lift the row: it stays where it was, faded, and its likeness comes along at the size it had. The ghost
        // hangs just below and right of the cursor rather than exactly where it was grabbed — grabbing a card in the
        // middle would otherwise park it right on top of the drop marker, hiding the one thing the drag is for.
        Rect bounds = ContainerAt(_dragOrigin) is { } source
            ? SourceBounds(source)
            : new Rect(position.X, position.Y, 220, 44);
        Point grabbedAt = position - bounds.Position;
        _grabOffset = new Point(Math.Min(grabbedAt.X, GhostLead), Math.Min(grabbedAt.Y, GhostLead));

        _dragging.IsDragging = true;
        _ghost.Width = bounds.Width;
        _ghost.Height = bounds.Height;
        _ghostContent.Content = _dragging;
        _ghost.IsVisible = true;

        // Escape has to reach us wherever this content is parented, and a drag holds the pointer, not the focus.
        _keyHost = TopLevel.GetTopLevel(_members);
        _keyHost?.AddHandler(KeyDownEvent, OnKeyDownDuringDrag, RoutingStrategies.Tunnel);
    }

    private void TrackDrag(Point position)
    {
        if (_ghost is null)
            return;

        Point inLayer = ToLayer(position);
        Canvas.SetLeft(_ghost, inLayer.X - _grabOffset.X);
        Canvas.SetTop(_ghost, inLayer.Y - _grabOffset.Y);

        _insertionIndex = InsertionIndexAt(position);
        ShowMarker();
    }

    /// <summary>Where the member would land: in front of the row under the cursor, or behind it once the cursor is
    /// past that row's middle. Past the last row — the empty space below or beside the list — it lands at the end.</summary>
    internal int InsertionIndexAt(Point position)
    {
        if (_members is null)
            return 0;

        int count = _members.ItemCount;
        if (ContainerAt(position) is not { } container)
            return count;

        int index = _members.IndexFromContainer(container);
        if (index < 0)
            return count;

        Rect bounds = SourceBounds(container);
        bool pastMiddle = DragsHorizontally ? position.X > bounds.Center.X : position.Y > bounds.Center.Y;
        return pastMiddle ? index + 1 : index;
    }

    private void ShowMarker()
    {
        if (_marker is null || _members is null)
            return;

        int count = _members.ItemCount;
        if (count == 0)
        {
            _marker.IsVisible = false;
            return;
        }

        // Anchor on a real row: the one the member would slot in front of, or the last one when it lands at the end.
        bool afterLast = _insertionIndex >= count;
        int anchorIndex = afterLast ? count - 1 : _insertionIndex;
        if (_members.ContainerFromIndex(anchorIndex) is not { } anchor)
        {
            _marker.IsVisible = false;
            return;
        }

        Rect bounds = SourceBounds(anchor);
        Point topLeft = ToLayer(bounds.Position);

        // Clamped into the layer: in front of the very first row the line would otherwise sit just outside it and be
        // clipped away, which is exactly the drop the user needs to see.
        if (DragsHorizontally)
        {
            Canvas.SetLeft(_marker, Math.Max(0, afterLast ? topLeft.X + bounds.Width : topLeft.X - MarkerThickness));
            Canvas.SetTop(_marker, topLeft.Y);
            _marker.Width = MarkerThickness;
            _marker.Height = bounds.Height;
        }
        else
        {
            Canvas.SetLeft(_marker, topLeft.X);
            Canvas.SetTop(_marker, Math.Max(0, afterLast ? topLeft.Y + bounds.Height : topLeft.Y - MarkerThickness));
            _marker.Width = bounds.Width;
            _marker.Height = MarkerThickness;
        }

        _marker.IsVisible = true;
    }

    private void OnMemberPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStarted && _dragging is { } dropped && ViewModel is { } viewModel)
        {
            viewModel.MoveMemberTo(dropped, _insertionIndex);
            viewModel.CommitOrder();
        }

        EndDrag();
    }

    // Losing the capture — the pointer left the window, another control took it — is a cancel, not a drop: the list
    // never moved, so putting the ghost away is the whole of it.
    private void OnMemberPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private void OnKeyDownDuringDrag(object? sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Escape)
            return;

        e.Handled = true;
        EndDrag();
    }

    internal void EndDrag()
    {
        if (_dragging is not null)
            _dragging.IsDragging = false;
        if (_ghost is not null)
            _ghost.IsVisible = false;
        if (_ghostContent is not null)
            _ghostContent.Content = null;
        if (_marker is not null)
            _marker.IsVisible = false;

        _keyHost?.RemoveHandler(KeyDownEvent, OnKeyDownDuringDrag);
        _keyHost = null;
        _dragging = null;
        _dragStarted = false;
    }

    // The item container under a point, whichever layout drew it: walk up from what was hit until we reach a direct
    // child of the items panel.
    private Control? ContainerAt(Point point)
    {
        if (_members?.InputHitTest(point) is not Visual hit)
            return null;

        for (Visual? visual = hit; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control candidate && ReferenceEquals(candidate.GetVisualParent(), _members.ItemsPanelRoot))
                return candidate;

        return null;
    }

    // A container's bounds in the ItemsControl's own space, so the ghost, the marker and the pointer all agree
    // however far the list is scrolled.
    private Rect SourceBounds(Control container) =>
        new(container.TranslatePoint(default, _members!) ?? default, container.Bounds.Size);

    private Point ToLayer(Point inMembers) =>
        (_members is null || _dragLayer is null ? null : _members.TranslatePoint(inMembers, _dragLayer)) ?? inMembers;
}
