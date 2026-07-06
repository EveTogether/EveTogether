namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Host seam for the pivot's pre-flight: token storage and granted scopes differ per host
/// (client = <c>IPerCharacterTokenStore</c> + <c>Character.GrantedScopes</c>; server = the encrypted
/// <c>ServerAuthRepository</c> + <c>SyncedCharacter.GrantedScopes</c>). Implementations check the
/// granted scopes, validate the token, auto-refresh when expiring, and hand back the bearer —
/// so the host difference lives in DI, not in the pivot (anti-splintering).
/// </summary>
public interface IEsiTokenProvider
{
    /// <summary>
    /// Verifies the character holds every <paramref name="requiredScopes"/> entry and has a usable
    /// access token (refreshing if needed). Returns the bearer on success, or why it could not.
    /// </summary>
    Task<EsiAuthorization> AuthorizeAsync(
        int characterId,
        IReadOnlyList<string> requiredScopes,
        CancellationToken cancellationToken = default);
}
