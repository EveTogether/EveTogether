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
        // Two different answers, and the difference is the whole of ET-308. Only a missing token or a refresh the SSO
        // refused means "sign in again". Everything else is a renewal still in progress — SSO unreachable, a
        // clock-skew back-off, or a token ESI refused moments ago whose forced refresh is on cooldown — and reads as
        // AuthPending: the call is skipped cleanly (no 401 collected, no error logged per tick) and the consumer tries
        // again later instead of telling the pilot their sign-in expired.
        if (status is TokenStatus.NoToken or TokenStatus.NeedsReauth)
            return EsiAuthorization.AuthRequired;
        if (status is TokenStatus.TemporarilyUnavailable or TokenStatus.Rejected or TokenStatus.Reconnecting)
            return EsiAuthorization.AuthPending;

        var tokens = await tokenStore.LoadAsync(characterId, cancellationToken);
        return tokens is null
            ? EsiAuthorization.AuthRequired
            : EsiAuthorization.Authorized(tokens.AccessToken);
    }

    public Task TokenRefusedAsync(int characterId, CancellationToken cancellationToken = default) =>
        refreshService.RecordRefusalAsync(characterId, cancellationToken);
}
