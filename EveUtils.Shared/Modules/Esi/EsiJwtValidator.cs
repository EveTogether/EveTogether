using System.Linq;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// Validates the EVE SSO access-token JWT: issuer, audience (must contain the client_id and
/// "EVE Online"), lifetime, and the signature against EVE's published JWKS with the algorithm pinned
/// to RS256 (no alg/kid confusion). The signing keys are fetched + cached via ConfigurationManager.
/// </summary>
public sealed class EsiJwtValidator : IEsiJwtValidator, ISingletonService
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configuration =
        new(EsiEndpoints.Metadata, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever());

    public async Task<EsiIdentity> ValidateAsync(string accessToken, string clientId, CancellationToken cancellationToken = default)
    {
        var openId = await _configuration.GetConfigurationAsync(cancellationToken);

        var parameters = new TokenValidationParameters
        {
            ValidIssuers = [EsiEndpoints.Issuer, EsiEndpoints.IssuerHttps],
            ValidAudiences = [clientId, EsiEndpoints.ExpectedAudience],
            IssuerSigningKeys = openId.SigningKeys,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidAlgorithms = [EsiEndpoints.SigningAlgorithm] // pin RS256 — reject alg=none / HS256 confusion
        };

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(accessToken, parameters);
        if (!result.IsValid)
            throw new InvalidOperationException("ESI access token failed validation.", result.Exception);

        var jwt = (JsonWebToken)result.SecurityToken;

        // audience must actually contain our client_id (not only "EVE Online").
        if (!jwt.Audiences.Contains(clientId))
            throw new InvalidOperationException("ESI access token audience does not contain the client id.");

        var subject = jwt.GetClaim("sub").Value; // "CHARACTER:EVE:<id>"
        var characterId = ParseCharacterId(subject);
        var name = jwt.TryGetClaim("name", out var nameClaim) ? nameClaim.Value : "Unknown";

        // extract the granted scope set from the scp claim.
        // EVE serialises scopes as a JSON array when > 1, so each element becomes a separate Claim
        // with key "scp". Collect all of them to handle both single and multi-scope responses.
        var scopes = jwt.Claims
            .Where(c => string.Equals(c.Type, "scp", StringComparison.Ordinal))
            .Select(c => c.Value)
            .ToArray();

        return new EsiIdentity(characterId, name, scopes);
    }

    private static int ParseCharacterId(string subject)
    {
        var separator = subject.LastIndexOf(':');
        return separator >= 0 && int.TryParse(subject[(separator + 1)..], out var id)
            ? id
            : throw new InvalidOperationException($"Unexpected ESI subject claim '{subject}'.");
    }
}
