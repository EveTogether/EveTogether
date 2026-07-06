namespace EveUtils.Shared.Modules.Esi;

/// <summary>Validates an EVE SSO access-token JWT and returns the verified identity.</summary>
public interface IEsiJwtValidator
{
    Task<EsiIdentity> ValidateAsync(string accessToken, string clientId, CancellationToken cancellationToken = default);
}
