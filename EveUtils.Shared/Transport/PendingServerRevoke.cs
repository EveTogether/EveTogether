namespace EveUtils.Shared.Transport;

/// <summary>
/// A decouple the server could not be told about. The local session is gone, but the server still holds one for the
/// character until it hears about it, so the revoke is kept here and repeated on the next connection to that server
/// instead of being dropped. Client-only — applied by the ClientDbContext.
/// </summary>
public sealed class PendingServerRevoke
{
    public int Id { get; set; }
    public string Address { get; set; } = string.Empty;
    public int CharacterId { get; set; }

    /// <summary>The access token of the session to revoke — what <c>Session.Revoke</c> identifies a session by. Nothing
    /// rotates it any more: the local session that would have been refreshed is already removed.</summary>
    public string AccessToken { get; set; } = string.Empty;
}
