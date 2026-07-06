namespace EveUtils.Shared.Transport;

/// <summary>
/// EF-persisted coupled server: the SQLite-backed client row that merges the former
/// <c>servers.json</c> (label + the server's own name) and <c>server-trust.json</c> (pinned TLS
/// fingerprint). Client-only — applied by the ClientDbContext, so it lands in the client
/// SQLite only. Keyed by the raw server address. Table name = entity name ("CoupledServer"), per the
/// project convention (no ToTable — Shared references base EF Core only).
/// </summary>
public sealed class CoupledServer
{
    public string Address { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? ServerName { get; set; }

    /// <summary>Pinned TLS cert fingerprint (SHA-256 hex). Null until the first TOFU pairing pins it.</summary>
    public string? CertFingerprint { get; set; }
}
