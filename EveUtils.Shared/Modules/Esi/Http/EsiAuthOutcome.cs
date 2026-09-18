namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>Result of the pivot's pre-flight auth check for an authed ESI call.</summary>
public enum EsiAuthOutcome
{
    /// <summary>Scopes granted and a valid token is available — the call may proceed.</summary>
    Authorized,

    /// <summary>The character was never granted a required scope — do not send the call.</summary>
    ScopeMissing,

    /// <summary>No token, or EVE SSO refused the refresh — the character must re-authenticate.</summary>
    AuthRequired,

    /// <summary>The token is being renewed and cannot be used right now (SSO unreachable, backing off, or just refused
    /// by ESI with its renewal pending) — skip this call and try again, no re-authentication needed (ET-308).</summary>
    AuthPending
}
