using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Server.DataExplorer;

/// <summary>
/// The one status an admin scans a fleet list by. <see cref="Fleet.State"/> (soft delete) and
/// <see cref="Fleet.Activation"/> (in-game phase) are independent columns, but an archived fleet's phase no longer
/// matters, so archived wins and otherwise the phase shows. Declaration order is the list's group order.
/// </summary>
public enum FleetPanelStatus
{
    InOp = 0,
    Forming = 1,
    Concluded = 2,
    Archived = 3,
}
