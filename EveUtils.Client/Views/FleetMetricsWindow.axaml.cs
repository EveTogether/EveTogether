using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// Free-standing fleet-metrics window: one live DPS graph per active member plus the fleet roll-up
/// totals. Non-modal so its graphs keep updating beside the main + fleets windows; disposes its view-model (drops
/// the bus subscription) on close.
/// </summary>
public partial class FleetMetricsWindow : ChromedWindow
{
    // Far enough that a click on a member's pop-out button, or a shaky press, is not read as the start of a drag.
    private const double DragThreshold = 4;

    private DpsViewModel? _dragging;
    private Point _dragOrigin;
    private bool _dragStarted;

    public FleetMetricsWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Reordering is wired once, on the one ItemsControl, rather than per item template: list, grid and compact
        // then drag identically and a fourth density would need no gesture code of its own. The handlers travel with
        // the control, so they keep working when the module host reparents this content into a docked tab.
        if (this.FindControl<ItemsControl>("MemberList") is { } members)
        {
            members.PointerPressed += OnMemberPointerPressed;
            members.PointerMoved += OnMemberPointerMoved;
            members.PointerReleased += OnMemberPointerReleased;
            members.PointerCaptureLost += OnMemberPointerCaptureLost;
        }
    }

    public FleetMetricsWindow(FleetMetricsViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    private FleetMetricsViewModel? ViewModel => DataContext as FleetMetricsViewModel;

    // The member under a point, whichever layout drew it: every element inside an item template carries that
    // member as its DataContext, and the gaps between rows carry the screen's own.
    private static DpsViewModel? MemberAt(ItemsControl members, Point point) =>
        (members.InputHitTest(point) as StyledElement)?.DataContext as DpsViewModel;

    private void OnMemberPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ItemsControl members || !e.GetCurrentPoint(members).Properties.IsLeftButtonPressed)
            return;

        // A button inside the row (the pop-out) marks the press handled, so this never sees it.
        _dragOrigin = e.GetPosition(members);
        _dragging = MemberAt(members, _dragOrigin);
        _dragStarted = false;
    }

    private void OnMemberPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is null || sender is not ItemsControl members)
            return;

        Point position = e.GetPosition(members);
        if (!_dragStarted)
        {
            if (Point.Distance(position, _dragOrigin) < DragThreshold)
                return;
            _dragStarted = true;
            e.Pointer.Capture(members);
        }

        // Reorder as the pointer travels rather than only on release: the row moving out from under the cursor is
        // the feedback, so no drag ghost or drop marker has to be invented to say where it would land.
        if (MemberAt(members, position) is { } target && !ReferenceEquals(target, _dragging))
            ViewModel?.MoveMember(_dragging, target);
    }

    private void OnMemberPointerReleased(object? sender, PointerReleasedEventArgs e) => EndDrag();

    private void OnMemberPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    // Remember whatever order now stands, including when the capture was taken away mid-drag: the rows have already
    // moved, so leaving it unsaved would be the one outcome the user cannot see.
    private void EndDrag()
    {
        if (_dragStarted)
            ViewModel?.CommitOrder();

        _dragging = null;
        _dragStarted = false;
    }
}
