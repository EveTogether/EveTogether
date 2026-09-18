using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Server.DataExplorer;

/// <summary>One fleet row: the fleet plus what the list shows about its relations without opening it.</summary>
public sealed class FleetListItem
{
    /// <summary>A fleet that is not archived but has had no activity for this long is flagged stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    public required Fleet Fleet { get; init; }
    public required IReadOnlyList<int> MemberCharacterIds { get; init; }

    /// <summary>Null when the fleet has no composition or when the one it points at is gone.</summary>
    public string? CompositionName { get; init; }

    /// <summary><c>FleetCompositionId</c> has no FK, so deleting a composition leaves the fleet pointing at nothing.</summary>
    public bool CompositionMissing => Fleet.FleetCompositionId is not null && CompositionName is null;

    public FleetPanelStatus Status => StatusOf(Fleet);

    public bool IsStale(DateTimeOffset now) => Status != FleetPanelStatus.Archived && now - Fleet.LastActivityAt > StaleAfter;

    public static FleetPanelStatus StatusOf(Fleet fleet) => fleet.State == FleetState.Archived
        ? FleetPanelStatus.Archived
        : fleet.Activation switch
        {
            FleetActivation.Active => FleetPanelStatus.InOp,
            FleetActivation.Concluded => FleetPanelStatus.Concluded,
            _ => FleetPanelStatus.Forming,
        };
}
