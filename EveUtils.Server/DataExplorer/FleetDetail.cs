using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Server.DataExplorer;

/// <summary>A fleet with its roster and its composition, loaded only for the fleet an admin has opened.</summary>
public sealed class FleetDetail
{
    public required Fleet Fleet { get; init; }
    public required IReadOnlyList<FleetWing> Wings { get; init; }
    public required IReadOnlyList<FleetSquad> Squads { get; init; }
    public required IReadOnlyList<FleetMember> Members { get; init; }
    public FleetComposition? Composition { get; init; }
    public required IReadOnlyList<RoleCoverage> Coverage { get; init; }

    /// <summary>The members' <c>AssignedFit.ServerSharedFitId</c>s that still exist, so only those become links.</summary>
    public required IReadOnlySet<int> ExistingSharedFitIds { get; init; }
}
