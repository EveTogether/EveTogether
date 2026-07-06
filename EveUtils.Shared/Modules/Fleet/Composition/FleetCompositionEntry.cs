namespace EveUtils.Shared.Modules.Fleet.Composition;

/// <summary>
/// A single allowed fit within a <see cref="FleetCompositionRole"/>. Multiple entries per role is the core
/// of the model ("choose from the allowed fits until the minimum is met", not "pick one alternative").
/// <see cref="EntryMinCount"/> is the optional per-fit minimum (e.g. "≥3 Guardian"); null = no per-fit minimum.
/// <see cref="Fit"/> is an embedded self-contained snapshot (owned columns), so the entry survives the source
/// library fit being deleted.
/// </summary>
public sealed class FleetCompositionEntry
{
    public long Id { get; set; }
    public long RoleId { get; set; }

    /// <summary>Per-fit minimum (e.g. "≥3 Guardian"); null = no per-fit minimum.</summary>
    public int? EntryMinCount { get; set; }

    public int SortOrder { get; set; }

    public FitReference Fit { get; set; } = null!;
}
