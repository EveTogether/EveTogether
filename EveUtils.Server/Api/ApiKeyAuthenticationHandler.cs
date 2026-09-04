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
    private const string Expired = "The API key has expired.";

    /// <summary>What the 401 will say. A field because the challenge is a second call into this same per-request
    /// handler, and by then the reason the key failed is no longer on the table.</summary>
    private string _reason = Rejected;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? presented = ApiKeyAuthentication.Presented(Request);
        if (presented is null)
            return AuthenticateResult.NoResult();

        if (!ApiKeySecurity.TryParse(presented, out var prefix, out var secret))
            return AuthenticateResult.Fail(Rejected);

        ApiKey? key = await repository.FindByPrefixAsync(prefix, Context.RequestAborted);
        if (key is null || !ApiKeySecurity.Verify(secret, key.SecretHash))
            return AuthenticateResult.Fail(Rejected);

        var now = DateTimeOffset.UtcNow;
        if (!key.IsActive)
            return AuthenticateResult.Fail(Rejected);
        if (key.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            // Named, unlike the others: reaching this branch already took the whole valid secret, so saying so
            // tells an attacker nothing and tells the owner of a dead key exactly what to do about it.
            _reason = Expired;
            return AuthenticateResult.Fail(Expired);
        }

        await repository.TouchLastUsedAsync(key.Id, now, Context.RequestAborted);

        // The audit line: who, when, and what they asked for. The prefix names the key without being it, and the
        // path is written without its query string — that is where ?apikey= would otherwise ride along.
        Logger.LogInformation("API key {Prefix} ({Label}) used {Method} {Path} from {Client}",
            key.Prefix, key.Label, Request.Method, Request.Path, Context.Connection.RemoteIpAddress);

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

    /// <summary>
    /// The 401 says why in the standard place, so a consumer can tell an expired key from a wrong one without the
    /// key itself appearing anywhere in the answer.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{Scheme.Name} error=\"invalid_key\", error_description=\"{_reason}\"";
        return Task.CompletedTask;
    }
}
