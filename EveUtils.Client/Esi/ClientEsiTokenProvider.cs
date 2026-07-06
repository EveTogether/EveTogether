using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.Esi;

/// <summary>
/// Client-side <see cref="IEsiTokenProvider"/>: granted scopes come from the local
/// <see cref="ICharacterRegistry"/> and the token from the per-character store, reusing
/// <see cref="ClientTokenRefreshService.EnsureValidAsync"/> for the validity + auto-refresh.
/// </summary>
public sealed class ClientEsiTokenProvider(
    ICharacterRegistry registry,
    IPerCharacterTokenStore tokenStore,
    ClientTokenRefreshService refreshService) : IEsiTokenProvider, ISingletonService
{
    public async Task<EsiAuthorization> AuthorizeAsync(
        int characterId,
        IReadOnlyList<string> requiredScopes,
        CancellationToken cancellationToken = default)
    {
        var character = (await registry.GetAllAsync(cancellationToken))
            .FirstOrDefault(c => c.EsiCharacterId == characterId);
        if (character is null)
            return EsiAuthorization.AuthRequired;

        foreach (var scope in requiredScopes)
            if (!character.HasScope(scope))
                return EsiAuthorization.ScopeMissing(scope);

        var status = await refreshService.EnsureValidAsync(characterId, cancellationToken);
        // TemporarilyUnavailable (refresh produced an unusable token, e.g. clock skew) is treated like "needs auth"
        // for this call: it fails cleanly with AuthRequired instead of throwing, so background pollers skip the cycle
        // quietly rather than logging an error on every tick.
        if (status is TokenStatus.NoToken or TokenStatus.NeedsReauth or TokenStatus.TemporarilyUnavailable)
            return EsiAuthorization.AuthRequired;

        var tokens = await tokenStore.LoadAsync(characterId, cancellationToken);
        return tokens is null
            ? EsiAuthorization.AuthRequired
            : EsiAuthorization.Authorized(tokens.AccessToken);
    }
}
