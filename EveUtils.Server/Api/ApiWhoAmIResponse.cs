using System.Security.Claims;

namespace EveUtils.Server.Api;

/// <summary>
/// Response for <c>GET /api/v1/whoami</c>: the key that opened the door, as the server sees it. Metadata only —
/// the prefix is the plaintext lookup handle, and the secret never appears here.
/// </summary>
public sealed record ApiWhoAmIResponse(
    string Prefix, string Label, IReadOnlyList<string> Scopes, int? OwnerCharacterId)
{
    /// <summary>Reads back the claims the API-key handler put on the principal.</summary>
    public static ApiWhoAmIResponse From(ClaimsPrincipal user) => new(
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
        user.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
        [.. user.FindAll(ApiKeyAuthentication.ScopeClaim).Select(claim => claim.Value)],
        int.TryParse(user.FindFirstValue(ApiKeyAuthentication.OwnerCharacterClaim), out var owner) ? owner : null);
}
