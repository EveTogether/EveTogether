namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>
/// A client-local, persisted snapshot of a public-ESI external-character lookup.
/// External-member info (name / corp / alliance) changes rarely, so the client caches each resolved id here and
/// only re-fetches from public ESI once the row is older than a day — sparing a public-ESI round-trip on every
/// re-open and surviving restarts. <see cref="FetchedAtUnixMs"/> is the Unix-ms stamp of the last successful
/// fetch. Client-only — applied by the ClientDbContext.
/// </summary>
public sealed class CachedExternalCharacter
{
    /// <summary>The ESI character id — the natural primary key (one row per external character).</summary>
    public int CharacterId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Corp { get; set; }

    public string? Alliance { get; set; }

    /// <summary>Unix-ms of the last successful public-ESI fetch — the freshness anchor for the 1-day TTL.</summary>
    public long FetchedAtUnixMs { get; set; }
}
