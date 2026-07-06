using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// Talks to the EVE SSO v2 token endpoint. PKCE public for the client (Mode A) and confidential
/// (Basic client_id:client_secret) for the server (Mode B). Always honours the returned refresh
/// token (rotation-proof). Uses the bare <see cref="EsiHttpClients.Auth"/> client (header handler
/// only) via the factory so this singleton never captures a transient HttpClient.
/// </summary>
public sealed class EsiAuthClient(IHttpClientFactory httpClientFactory) : IEsiAuthClient, ISingletonService
{
    public Task<EsiTokenSet> ExchangePublicAsync(string code, Pkce pkce, string clientId, CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["code_verifier"] = pkce.Verifier
        };
        return ExchangeAsync(form, authorization: null, cancellationToken);
    }

    public Task<EsiTokenSet> ExchangePkceConfidentialAsync(string code, Pkce pkce, string clientId, string clientSecret, CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = pkce.Verifier
        };
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));
        return ExchangeAsync(form, new AuthenticationHeaderValue("Basic", basic), cancellationToken);
    }

    public Task<EsiTokenSet> ExchangeConfidentialAsync(string code, string clientId, string clientSecret, CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code
        };
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));
        return ExchangeAsync(form, new AuthenticationHeaderValue("Basic", basic), cancellationToken);
    }

    public Task<EsiTokenSet> RefreshAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };

        AuthenticationHeaderValue? authorization = null;
        if (!string.IsNullOrEmpty(clientSecret))
        {
            // Confidential client (server, Mode B): credentials go in the Basic header ONLY.
            // EVE SSO rejects credentials supplied in both the header and the body
            // (400 invalid_request "Client credentials should only be provided once").
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));
            authorization = new AuthenticationHeaderValue("Basic", basic);
        }
        else
        {
            // Public client (PKCE, Mode A): no secret, so client_id travels in the body.
            form["client_id"] = clientId;
        }

        return ExchangeAsync(form, authorization, cancellationToken);
    }

    private async Task<EsiTokenSet> ExchangeAsync(
        Dictionary<string, string> form, AuthenticationHeaderValue? authorization, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, EsiEndpoints.Token)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Authorization = authorization;
        request.Headers.Host = "login.eveonline.com";

        var httpClient = httpClientFactory.CreateClient(EsiHttpClients.Auth);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ESI token exchange failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var accessToken = root.GetProperty("access_token").GetString()!;
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 1200;

        return new EsiTokenSet(accessToken, refreshToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }
}
