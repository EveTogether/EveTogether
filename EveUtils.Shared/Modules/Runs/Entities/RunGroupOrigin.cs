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

    /// <summary>The server <see cref="FleetId"/> lives on (ET-245) — a fleet id alone is only unique per server, and a
    /// client-only fleet's id comes from this client's own table. Null while this client could not tell for certain
    /// (a client-only fleet, the same id on two servers, an origin recorded before this column), and a run in such a
    /// group is then never published by itself: the server is written once when it is known, never guessed.</summary>
    public string? ServerAddress { get; set; }
}
