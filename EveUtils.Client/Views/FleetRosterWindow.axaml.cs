using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Views;

/// <summary>
/// Per-fleet roster window: a 30/70 split of the member list (accepted + pending invites + join
/// requests) and the FC › wing › squad › members tree, with owner roster actions and the Start lifecycle. Non-modal
/// so it stays usable beside the fleets window. Members can be dragged onto a tree node to move them — or onto an
/// occupied commander slot to swap (stream G / G-3); the right-click cascade stays as the precise alternative.
/// </summary>
public partial class FleetRosterWindow : ChromedWindow
{
    // The dragged roster member's id travels as a string (DataFormat<T> needs a reference type), stream G / G-3.
    private static readonly DataFormat<string> MemberFormat = DataFormat.CreateInProcessFormat<string>("eveutils-roster-member-id");

    public FleetRosterWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Roster drag-and-drop (G-3): begin a drag from a member row, drop onto a tree node to move/swap. Attached to
        // the content root (not the window) so it keeps working when the window is re-hosted in the docked module host.
        if (Content is Control content)
        {
            content.AddHandler(PointerPressedEvent, OnRosterPointerPressed, RoutingStrategies.Tunnel);
            content.AddHandler(DragDrop.DragOverEvent, OnRosterDragOver);
            content.AddHandler(DragDrop.DropEvent, OnRosterDrop);
        }
    }

    public FleetRosterWindow(FleetRosterViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose(); // release the fleet.changed subscription when the window closes
    }

    // Begin a drag carrying the pressed member row's id. Owner-only; a press on a button (PICK FIT, accept/decline, the
    // tree expander) or a non-member row starts no drag, so those interactions keep working. Avalonia 12 only lets a
    // drag begin from a press event.
    private async void OnRosterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not FleetRosterViewModel { IsOwner: true } ||
            !e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            return;

        var chain = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().ToList();
        // A press on a button (PICK FIT, accept/decline), the tree expander (ToggleButton) or a menu item never starts a
        // drag, so those interactions keep working.
        if (chain is null || chain.Any(c => c is Button or ToggleButton or MenuItem))
            return;

        var memberId = chain.Select(c => MemberIdOf(c.DataContext)).FirstOrDefault(id => id is not null);
        if (memberId is null)
            return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(MemberFormat, memberId.Value.ToString(CultureInfo.InvariantCulture)));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        _HighlightDropTarget(null);   // drag ended (dropped or cancelled) → clear the target highlight
    }

    private void OnRosterDragOver(object? sender, DragEventArgs e)
    {
        var isMember = e.DataTransfer.Contains(MemberFormat);
        e.DragEffects = isMember ? DragDropEffects.Move : DragDropEffects.None;
        _HighlightDropTarget(isMember
            ? (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()
            : null);
        e.Handled = true;
    }

    private TreeViewItem? _dropTarget;

    // Tints the tree node the drop would land on so the move/swap target is obvious mid-drag (stream G / G-3). Background
    // is set directly (not a pseudo-class) so it shows regardless of theme, and cleared back to the template default.
    private void _HighlightDropTarget(TreeViewItem? item)
    {
        if (ReferenceEquals(item, _dropTarget))
            return;
        _dropTarget?.ClearValue(TemplatedControl.BackgroundProperty);
        _dropTarget = item;
        if (_dropTarget is not null && this.TryFindResource("AccentSoftBrush", out var brush) && brush is IBrush accent)
            _dropTarget.Background = accent;
    }

    private void OnRosterDrop(object? sender, DragEventArgs e)
    {
        var raw = e.DataTransfer.TryGetValue(MemberFormat);
        if (DataContext is not FleetRosterViewModel viewModel ||
            raw is null ||
            !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var draggedMemberId))
            return;

        var target = (e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(control => control.DataContext)
            .FirstOrDefault(IsDropTarget);
        if (target is null)
            return;

        _ = viewModel.HandleDropAsync(draggedMemberId, target);
        e.Handled = true;
    }

    // The id of a draggable roster row: a tree member node, or an accepted member in the left list.
    private static long? MemberIdOf(object? dataContext) => dataContext switch
    {
        MemberNodeViewModel node => node.Member.Id,
        RosterEntryViewModel { IsAccepted: true, Member: { } member } => member.Id,
        _ => null
    };

    // A drop only resolves against a roster structure node or a member row (the resolver no-ops on anything else).
    private static bool IsDropTarget(object? dataContext) =>
        dataContext is FleetRootNodeViewModel or WingNodeViewModel or SquadNodeViewModel or MemberNodeViewModel;
}
