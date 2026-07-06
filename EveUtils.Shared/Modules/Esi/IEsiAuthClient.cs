namespace EveUtils.Shared.Modules.Esi;

/// <summary>Exchanges an SSO authorization code for tokens (and refreshes them).</summary>
public interface IEsiAuthClient
{
    /// <summary>PKCE public exchange (client, Mode A): no client secret is sent.</summary>
    Task<EsiTokenSet> ExchangePublicAsync(string code, Pkce pkce, string clientId, CancellationToken cancellationToken = default);

    /// <summary>PKCE + confidential exchange (client, Mode A with a bundled app secret): Basic auth + code_verifier.</summary>
    Task<EsiTokenSet> ExchangePkceConfidentialAsync(string code, Pkce pkce, string clientId, string clientSecret, CancellationToken cancellationToken = default);

    /// <summary>Confidential exchange (server, Mode B): client_id + client_secret via Basic auth, no PKCE.</summary>
    Task<EsiTokenSet> ExchangeConfidentialAsync(string code, string clientId, string clientSecret, CancellationToken cancellationToken = default);

    /// <summary>Refreshes an existing token set using the stored refresh token.</summary>
    Task<EsiTokenSet> RefreshAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default);
}
