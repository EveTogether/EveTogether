using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// Resolves a roster drag-drop (stream G / G-3) against the node it landed on, deciding whether the dragged member
/// moves to a position or swaps with the member already occupying a commander slot. Pure and node-only so it is
/// trivially testable, separate from the window's transport calls in <see cref="FleetRosterViewModel.HandleDropAsync"/>.
/// </summary>
public static class RosterDropResolver
{
    public static RosterDropResolution Resolve(FleetMemberInfo dragged, object? targetNode) =>
        targetNode switch
        {
            FleetRootNodeViewModel root => _ResolveCommanderDrop(dragged, FleetRole.FleetCommander, -1, -1, root.Commander),
            WingNodeViewModel wing => _ResolveCommanderDrop(dragged, FleetRole.WingCommander, wing.WingId, -1, wing.Commander),
            SquadNodeViewModel squad => _ResolveMove(dragged, FleetRole.SquadMember, squad.WingId, squad.SquadId),
            MemberNodeViewModel node => _ResolveMemberDrop(dragged, node.Member),
            _ => RosterDropResolution.None
        };

    // The fleet/wing node targets its commander slot: an occupied one swaps (the two exchange exact positions),
    // an empty one is a plain move into the slot.
    private static RosterDropResolution _ResolveCommanderDrop(
        FleetMemberInfo dragged, FleetRole role, long wingId, long squadId, MemberNodeViewModel? commander)
    {
        if (commander is { } occupant)
            return occupant.Member.Id == dragged.Id
                ? RosterDropResolution.None
                : RosterDropResolution.Swap(occupant.Member.Id);
        return _ResolveMove(dragged, role, wingId, squadId);
    }

    // Dropping onto an occupied commander row swaps with them; onto a plain squad member, the dragged pilot joins that
    // squad. A wing/fleet-level plain member has no unique slot to target → no-op (drop on the node itself instead).
    private static RosterDropResolution _ResolveMemberDrop(FleetMemberInfo dragged, FleetMemberInfo target)
    {
        if (target.Id == dragged.Id)
            return RosterDropResolution.None;
        if (target.Role is FleetRole.FleetCommander or FleetRole.WingCommander or FleetRole.SquadCommander)
            return RosterDropResolution.Swap(target.Id);
        return target.SquadId >= 0
            ? _ResolveMove(dragged, FleetRole.SquadMember, target.WingId, target.SquadId)
            : RosterDropResolution.None;
    }

    // A move onto the dragged member's current position is a no-op — avoids a needless round-trip + reload.
    private static RosterDropResolution _ResolveMove(FleetMemberInfo dragged, FleetRole role, long wingId, long squadId) =>
        dragged.Role == role && dragged.WingId == wingId && dragged.SquadId == squadId
            ? RosterDropResolution.None
            : RosterDropResolution.Move(role, wingId, squadId);
}
