using System.Security.Claims;
using EveUtils.Shared.Modules.ApiKeys.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Primitives;

namespace EveUtils.Server.Api;

/// <summary>
/// Names for the API-key authentication scheme that guards <c>/api/v1</c>. A scheme of its own, deliberately
/// beside the gRPC bearer and the admin cookie rather than sharing anything with them.
/// </summary>
public static class ApiKeyAuthentication
{
    public const string Scheme = "ApiKey";

    /// <summary>Authorization policy on the <c>/api/v1</c> group: authenticated by this scheme and carrying
    /// the read scope. A key that authenticates without the scope gets 403, not 401.</summary>
    public const string Policy = "ApiKeyReadAll";

    public const string HeaderName = "X-API-KEY";

    /// <summary>Convenience for browsers and embeds. Proxies log query strings, so the value is never logged
    /// here and the header stays the documented default.</summary>
    public const string QueryName = "apikey";

    public const string ScopeClaim = "api_scope";
    public const string OwnerCharacterClaim = "api_owner_character_id";

    /// <summary>
    /// The character this key is scoped to, or null for a key with no owner — which is admin scope over all
    /// server data (ratified decision 3). The one place that reads this claim back off a principal.
    /// </summary>
    public static int? OwnerCharacterId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(OwnerCharacterClaim), out int owner) ? owner : null;

    /// <summary>The key as presented — header first, then the documented query fallback. The one place a request
    /// is read for a key, so the rate limiter and the auth handler cannot drift on what counts as one.</summary>
    public static string? Presented(HttpRequest request)
    {
        if (request.Headers.TryGetValue(HeaderName, out StringValues header) && !string.IsNullOrWhiteSpace(header))
            return header.ToString();

        return request.Query.TryGetValue(QueryName, out StringValues query) && !string.IsNullOrWhiteSpace(query)
            ? query.ToString()
            : null;
    }

    /// <summary>The public prefix of the presented key, or null when there is none to parse. This is what the
    /// rate limiter partitions on: it names the consumer without being the secret.</summary>
    public static string? PresentedPrefix(HttpRequest request) =>
        ApiKeySecurity.TryParse(Presented(request), out string prefix, out _) ? prefix : null;

    /// <summary>Only the API-key scheme counts on <c>/api/v1</c> — an admin cookie must not open a data route.
    /// A key that authenticates but lacks the scope fails here, which is a 403 rather than a 401.</summary>
    public static AuthorizationPolicy BuildPolicy() => new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(Scheme)
        .RequireAuthenticatedUser()
        .RequireClaim(ScopeClaim, ApiKeyScopes.ReadAll)
        .Build();
}
