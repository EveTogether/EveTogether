using EveUtils.Shared.Modules.ApiKeys.Services;
using Microsoft.AspNetCore.Authorization;

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

    /// <summary>Only the API-key scheme counts on <c>/api/v1</c> — an admin cookie must not open a data route.
    /// A key that authenticates but lacks the scope fails here, which is a 403 rather than a 401.</summary>
    public static AuthorizationPolicy BuildPolicy() => new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(Scheme)
        .RequireAuthenticatedUser()
        .RequireClaim(ScopeClaim, ApiKeyScopes.ReadAll)
        .Build();
}
