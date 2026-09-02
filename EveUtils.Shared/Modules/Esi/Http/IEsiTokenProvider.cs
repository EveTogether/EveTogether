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

    /// <summary>
    /// ESI refused the bearer this provider just handed out (401). The pre-flight cannot see that coming — it trusts
    /// the token's own expiry — so without this the provider would keep serving a token every call rejects, silently
    /// and indefinitely (ET-121). Implementations distrust their cached verdict for the character and let the next
    /// <see cref="AuthorizeAsync"/> refresh for real. A host that has nothing to reconsider leaves it as it is.
    /// </summary>
    Task TokenRefusedAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
