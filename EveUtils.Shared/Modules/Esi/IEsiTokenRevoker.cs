namespace EveUtils.Shared.Modules.Esi;

public interface IEsiTokenRevoker
{
    /// <summary>Invalidates a refresh token at EVE SSO (confidential client). Throws when CCP does not accept the call.</summary>
    Task RevokeRefreshTokenAsync(string refreshToken, string clientId, string clientSecret, CancellationToken cancellationToken = default);
}
