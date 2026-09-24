namespace EveUtils.Shared.Modules.Esi;

public interface IEsiTokenRevoker
{
    /// <summary>Invalidates a refresh token at EVE SSO: as a confidential client with a secret (the server), as a
    /// public PKCE client without one (the desktop client). Throws when CCP does not accept the call.</summary>
    Task RevokeRefreshTokenAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default);
}
