using System.Security.Claims;
using System.Text.Encodings.Web;
using EveUtils.Shared.Modules.ApiKeys.Entities;
using EveUtils.Shared.Modules.ApiKeys.Repositories;
using EveUtils.Shared.Modules.ApiKeys.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EveUtils.Server.Api;

/// <summary>
/// Validates the API key presented on <c>/api/v1</c>: parse the prefix, read that one row, hash the presented
/// secret and compare it in constant time, then check the key is active and unexpired. Every rejection answers
/// the same way — which stage failed is not the caller's business. The key and its hash are never logged,
/// never echoed and never put in a failure message.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IApiKeyRepository repository) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private const string Rejected = "Invalid API key.";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = _Presented();
        if (presented is null)
            return AuthenticateResult.NoResult();

        if (!ApiKeySecurity.TryParse(presented, out var prefix, out var secret))
            return AuthenticateResult.Fail(Rejected);

        ApiKey? key = await repository.FindByPrefixAsync(prefix, Context.RequestAborted);
        if (key is null || !ApiKeySecurity.Verify(secret, key.SecretHash))
            return AuthenticateResult.Fail(Rejected);

        var now = DateTimeOffset.UtcNow;
        if (!key.IsActive || key.ExpiresAt is { } expiresAt && expiresAt <= now)
            return AuthenticateResult.Fail(Rejected);

        await repository.TouchLastUsedAsync(key.Id, now, Context.RequestAborted);

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, key.Prefix),
            new(ClaimTypes.Name, key.Label),
            .. key.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(scope => new Claim(ApiKeyAuthentication.ScopeClaim, scope))
        ];
        if (key.OwnerCharacterId is { } ownerCharacterId)
            claims.Add(new Claim(ApiKeyAuthentication.OwnerCharacterClaim, ownerCharacterId.ToString()));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private string? _Presented()
    {
        if (Request.Headers.TryGetValue(ApiKeyAuthentication.HeaderName, out var header) &&
            !string.IsNullOrWhiteSpace(header.ToString()))
            return header.ToString();

        return Request.Query.TryGetValue(ApiKeyAuthentication.QueryName, out var query) &&
               !string.IsNullOrWhiteSpace(query.ToString())
            ? query.ToString()
            : null;
    }
}
