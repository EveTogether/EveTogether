using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.ViewModels;

/// <summary>The resolved outcome of dropping a dragged member on a roster node (stream G / G-3): a
/// <see cref="RosterDropAction.Move"/> carries the target role+wing+squad, a <see cref="RosterDropAction.Swap"/> carries
/// the member already occupying the target commander slot, and <see cref="RosterDropAction.None"/> is a no-op drop.</summary>
public readonly record struct RosterDropResolution(
    RosterDropAction Action, FleetRole Role, long WingId, long SquadId, long OtherMemberId)
{
    public static RosterDropResolution None => new(RosterDropAction.None, FleetRole.Unassigned, -1, -1, 0);

    public static RosterDropResolution Move(FleetRole role, long wingId, long squadId) =>
        new(RosterDropAction.Move, role, wingId, squadId, 0);

    public static RosterDropResolution Swap(long otherMemberId) =>
        new(RosterDropAction.Swap, FleetRole.Unassigned, -1, -1, otherMemberId);
}
