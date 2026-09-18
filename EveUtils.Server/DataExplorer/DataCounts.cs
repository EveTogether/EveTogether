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

    /// <summary>Distinct group codes among the runs that are not deleted.</summary>
    public required int RunGroups { get; init; }

    /// <summary>Runs flown without a group. Each is a row of its own in the Runs list.</summary>
    public required int SoloRuns { get; init; }

    public required int Sessions { get; init; }

    /// <summary>The rows of the Runs list: every group plus every run flown alone.</summary>
    public int RunRows => RunGroups + SoloRuns;
}
