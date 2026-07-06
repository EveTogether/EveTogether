namespace EveUtils.Shared.Modules.Esi;

/// <summary>Outcome of an ESI token validity check.</summary>
public enum TokenStatus
{
    /// <summary>Token present and still valid (no refresh needed).</summary>
    Valid,
    /// <summary>Token was expiring/expired and has been refreshed successfully.</summary>
    Refreshed,
    /// <summary>No token stored for this character (never signed in / removed).</summary>
    NoToken,
    /// <summary>Refresh failed (refresh token revoked/invalid) — the character must re-authenticate.</summary>
    NeedsReauth,
    /// <summary>
    /// Refresh succeeded at the HTTP level but the resulting token is currently unusable (it fails validation — almost
    /// always a local clock skew vs the token lifetime). This is transient and re-auth won't fix it, so the caller
    /// should skip the ESI call this cycle rather than prompt re-auth; the refresh service backs off before retrying.
    /// </summary>
    TemporarilyUnavailable
}
