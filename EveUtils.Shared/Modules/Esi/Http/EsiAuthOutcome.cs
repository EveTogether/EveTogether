namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>Result of the pivot's pre-flight auth check for an authed ESI call.</summary>
public enum EsiAuthOutcome
{
    /// <summary>Scopes granted and a valid token is available — the call may proceed.</summary>
    Authorized,

    /// <summary>The character was never granted a required scope — do not send the call.</summary>
    ScopeMissing,

    /// <summary>No token, or refresh failed — the character must re-authenticate.</summary>
    AuthRequired
}
