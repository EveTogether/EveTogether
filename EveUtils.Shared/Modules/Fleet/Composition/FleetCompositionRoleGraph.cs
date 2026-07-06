namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>A role-group with its fit-entries, in sort order.</summary>
public sealed record FleetCompositionRoleGraph(FleetCompositionRole Role, IReadOnlyList<FleetCompositionEntry> Entries);
