namespace EveUtils.Server.DataExplorer;

/// <summary>The sidebar's per-entity counts, plus how many of them need an admin's attention.</summary>
public sealed class DataCounts
{
    public required int Characters { get; init; }
    public required int Fleets { get; init; }

    /// <summary>Fleets pointing at a composition that no longer exists.</summary>
    public required int FleetsNeedingAttention { get; init; }

    public required int Compositions { get; init; }
    public required int SharedFits { get; init; }
    public required int Sessions { get; init; }
}
