namespace EveUtils.Shared.Modules.RunOutcomes.Enums;

/// <summary>What happened to the stored run outcomes, carried by <c>RunOutcomesChangedEvent</c>. Local only, so new
/// values may be added freely.</summary>
public enum RunOutcomesChangeKind
{
    Stored,
    Updated,
    Removed,

    /// <summary>A pilot opted out and every outcome they shared went (ET-514).</summary>
    Withdrawn,

    /// <summary>Outcomes past the retention window went.</summary>
    Pruned
}
