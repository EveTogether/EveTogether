namespace EveUtils.Shared.Modules.ServerAuth.Entities;

/// <summary>
/// A server-issued session bound to a paired character. Tokens are stored hashed (the server
/// never keeps the plaintext); the client presents the access token as a gRPC bearer. Heartbeat
/// updates <see cref="LastHeartbeat"/> for presence in the admin panel (~30s).
/// </summary>
public sealed class ServerSession
{
    public int Id { get; set; }
    public int SyncedCharacterId { get; set; }
    public SyncedCharacter? SyncedCharacter { get; set; }
    public string AccessTokenHash { get; set; } = string.Empty;
    public string RefreshTokenHash { get; set; } = string.Empty;
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>Access-token expiry (~1h). Gates <see cref="SyncedCharacter"/> bus auth; forces a refresh.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Hard session/refresh-token expiry, decoupled from the 1h access window. The cleanup
    /// purges on this, not on <see cref="ExpiresAt"/>, so a silent <c>Session.Refresh</c> still works the
    /// next morning instead of forcing a re-pair. Slid forward on each refresh.
    /// </summary>
    public DateTimeOffset RefreshExpiresAt { get; set; }

    public DateTimeOffset LastHeartbeat { get; set; }
}
