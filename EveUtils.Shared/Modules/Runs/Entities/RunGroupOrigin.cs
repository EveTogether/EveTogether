namespace EveUtils.Shared.Modules.Runs.Entities;

/// <summary>Which fleet a group code was minted for. Stamped once, at the moment the code first becomes known to
/// this client, and never overwritten — the only route from a fleet to its runs that does not require decomposing
/// the code itself (ET-182). A code minted before this table existed, or on another client that this pilot never
/// synced with, simply has no row here; that is a gap in what is known, not something to backfill by parsing.</summary>
public sealed class RunGroupOrigin
{
    public string GroupCode { get; set; } = null!;
    public long FleetId { get; set; }
    public DateTime RecordedAtUtc { get; set; }
}
