namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>A composition with its role-groups and their fit-entries, assembled for a read. Used by the
/// transport seam to ship the whole doctrine in one call (the entities carry no navigation properties).</summary>
public sealed record FleetCompositionGraph(FleetComposition Composition, IReadOnlyList<FleetCompositionRoleGraph> Roles);
