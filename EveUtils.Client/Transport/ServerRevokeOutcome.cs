namespace EveUtils.Client.Transport;

public enum ServerRevokeOutcome
{
    /// <summary>The server removed the session.</summary>
    Revoked,

    /// <summary>The server answered and holds no such session (already swept, revoked or deleted) — nothing left to do.</summary>
    NoSuchSession,

    /// <summary>The server could not be reached, so it has not heard about the decouple.</summary>
    Unreachable
}
