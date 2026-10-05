using System.Text.Json;

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
    /// The SSO itself refused the grant: a 400/401 whose OAuth error is <c>invalid_grant</c>. Only this means "sign in
    /// again". A 5xx, a 429, an empty or HTML answer from something in between, or another OAuth error
    /// (<c>invalid_client</c>, <c>invalid_request</c> — a fault of the app, not of this sign-in) says nothing about the
    /// refresh token, and signing in again would not fix it (ET-445).
    /// </summary>
    public bool IsDefinitiveRejection => StatusCode is 400 or 401 && OAuthError() == "invalid_grant";

    private string? OAuthError()
    {
        try
        {
            using var document = JsonDocument.Parse(Body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("error", out var error)
                   && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
