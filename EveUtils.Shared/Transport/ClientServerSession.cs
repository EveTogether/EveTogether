namespace EveUtils.Shared.Transport;

/// <summary>
/// EF-persisted client-side server session: the SQLite-backed client row replacing
/// <c>server-sessions.json</c>. One row per (server address, character) so multiple characters can be
/// paired to the same server. Named <c>ClientServerSession</c> to avoid colliding with the
/// server-side <c>ServerSession</c> entity. Client-only — applied by the ClientDbContext.
/// <para>POC note: the server-issued session token is short-lived and stored plaintext here (parity with
/// the former JSON file); a real build would protect it like the encrypted ESI tokens.</para>
/// </summary>
public sealed class ClientServerSession
{
    public string Address { get; set; } = string.Empty;
    public int CharacterId { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;

    /// <summary>Unix-ms of the last save, so the bus can pick the most recently stored session for a
    /// server (replaces the former dictionary insertion-order heuristic). Stored as a long — SQLite
    /// orders it natively, unlike DateTimeOffset.</summary>
    public long SavedAtUnixMs { get; set; }
}
