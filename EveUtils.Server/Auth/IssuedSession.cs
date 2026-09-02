namespace EveUtils.Server.Auth;

/// <summary>A freshly issued server session: the plaintext tokens handed to the client (stored hashed), plus the
/// id of the row they belong to. The client keeps that id because it outlives every rotation the tokens do not,
/// and presents it on the next refresh so a refusal can say which kind it is (ET-123).</summary>
public sealed record IssuedSession(string AccessToken, string RefreshToken, int SessionId);

/// <summary>Why the server would not renew a session — the one thing the client needs in order to know whether
/// trying again could ever work.</summary>
public enum SessionRefusalReason
{
    /// <summary>Not a refusal.</summary>
    None,

    /// <summary>The presented token is not the current one, but the session is still there. This is the case
    /// ET-121 built the slow retry for, and it must keep behaving exactly as it did.</summary>
    Retry,

    /// <summary>The server has no usable session here any more — swept as abandoned, revoked from the panel, or
    /// past its refresh window. Retrying is pointless; the user has to couple the character again.</summary>
    SessionGone
}

/// <summary>What a <c>Session.Refresh</c> came to: the rotated pair, or the reason there is none.</summary>
public readonly record struct SessionRefreshResult(IssuedSession? Issued, SessionRefusalReason Refusal)
{
    public static SessionRefreshResult Ok(IssuedSession issued) => new(issued, SessionRefusalReason.None);
    public static SessionRefreshResult Refused(SessionRefusalReason reason) => new(null, reason);
}
