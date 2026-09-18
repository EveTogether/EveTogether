namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// EVE SSO answered the token endpoint with a non-success status. Carries the status and body so a caller can tell a
/// verdict about the refresh token (<see cref="IsDefinitiveRejection"/>) from an SSO that is merely having a bad
/// moment — the two used to be one string, which is how a DNS failure at wake-up ended up reading as an expired
/// sign-in (ET-308). Still an <see cref="InvalidOperationException"/> with the same message, so callers that match on
/// the text keep working.
/// </summary>
public sealed class EsiTokenExchangeException(int statusCode, string body)
    : InvalidOperationException($"ESI token exchange failed ({statusCode}): {body}")
{
    public int StatusCode { get; } = statusCode;

    public string Body { get; } = body;

    /// <summary>
    /// The SSO itself refused the grant: a 400/401 with a real body (<c>invalid_grant</c> and friends). Only this
    /// means "sign in again" — a 5xx, a 429 or an empty answer from something in between says nothing about the
    /// refresh token.
    /// </summary>
    public bool IsDefinitiveRejection =>
        StatusCode is 400 or 401 && !string.IsNullOrWhiteSpace(Body);
}
