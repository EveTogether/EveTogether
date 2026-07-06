using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One row in the roster's left-hand member list: an accepted member, a pending invite, or a pending
/// join-request, each with a status badge. Accepted members carry their <see cref="Member"/> so the move action can
/// target them; pending rows are informational (answered via invite flow / inbox).
/// </summary>
public sealed class RosterEntryViewModel
{
    private RosterEntryViewModel(
        string name, string badge, bool isAccepted, FleetMemberInfo? member, long? joinRequestId,
        MemberNodeViewModel? node = null)
    {
        Name = name;
        Badge = badge;
        IsAccepted = isAccepted;
        Member = member;
        JoinRequestId = joinRequestId;
        Node = node;
    }

    public string Name { get; }

    /// <summary>Short status badge: "accepted", "⧖ pending", "extern", or "⧖ request".</summary>
    public string Badge { get; }

    /// <summary>True for an accepted roster member — only these are move/manage targets.</summary>
    public bool IsAccepted { get; }

    /// <summary>The source member for an accepted row; null for a pending invite/request row.</summary>
    public FleetMemberInfo? Member { get; }

    /// <summary>The pending join-request's id for a request row; null otherwise. Set → Accept/Decline are offered.</summary>
    public long? JoinRequestId { get; }

    /// <summary>True for a pending join-request row — only these show the owner's Accept/Decline buttons.</summary>
    public bool IsJoinRequest => JoinRequestId is not null;

    /// <summary>The full member node behind an accepted row, so the left list offers the same manage menu as the
    /// tree. An unplaced member has no structure node — this is their only manage surface.</summary>
    public MemberNodeViewModel? Node { get; }

    /// <summary>True when this row carries the owner's manage menu (accepted member, viewed by the owner).</summary>
    public bool CanManage => Node is { IsOwner: true };

    public static RosterEntryViewModel Accepted(FleetMemberInfo member, string name, MemberNodeViewModel node) =>
        new(name, PositionBadge(member), isAccepted: true, member, null, node);

    /// <summary>The member's place in the composition (R3-4): role abbrev, or "unassigned" when in the fleet without a
    /// wing/squad, so the left list makes clear where (or that) someone already sits. Externals are tagged too.</summary>
    private static string PositionBadge(FleetMemberInfo member)
    {
        var role = member.Role switch
        {
            FleetRole.FleetCommander => "FC",
            FleetRole.WingCommander => "WC",
            FleetRole.SquadCommander => "SC",
            FleetRole.Unassigned => "unassigned",
            _ => "member"
        };
        return member.IsExternal ? $"{role} · extern" : role;
    }

    public static RosterEntryViewModel PendingInvite(string name) =>
        new(name, "⧖ pending", isAccepted: false, null, null);

    public static RosterEntryViewModel JoinRequest(long requestId, string name) =>
        new(name, "⧖ request", isAccepted: false, null, requestId);
}
