namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>
/// A role-group within a <see cref="FleetComposition"/>: a labelled bucket of fit-entries with an optional
/// group-minimum. <see cref="GroupMinCount"/> is the "≥40 DPS total, mix is free" rule (null = no group minimum);
/// individual fits in the group may additionally carry their own <c>EntryMinCount</c>. Role labels are free strings
/// (no hard enum) so the model stays open for extension.
/// </summary>
public sealed class FleetCompositionRole
{
    public long Id { get; set; }
    public long CompositionId { get; set; }

    public string RoleName { get; set; } = string.Empty;

    /// <summary>Group-minimum across all fit-entries in this role (e.g. "≥40 DPS total"); null = no group minimum.</summary>
    public int? GroupMinCount { get; set; }

    public int SortOrder { get; set; }
}
