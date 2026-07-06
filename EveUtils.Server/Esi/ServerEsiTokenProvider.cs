using System.Collections.Concurrent;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Esi;

/// <summary>
/// Server-side <see cref="IEsiTokenProvider"/>: granted scopes + the encrypted refresh token
/// come from <see cref="IServerAuthRepository"/>. The access token is never persisted, so it
/// is minted on demand via <see cref="IEsiAuthClient.RefreshAsync"/> and cached in-memory until it expires.
/// </summary>
public sealed class ServerEsiTokenProvider(
    IServiceScopeFactory scopeFactory,
    IEsiAuthClient authClient,
    EsiOptions esiOptions,
    ILogger<ServerEsiTokenProvider> logger) : IEsiTokenProvider, ISingletonService
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<int, EsiTokenSet> _accessCache = new();

    public async Task<EsiAuthorization> AuthorizeAsync(
        int characterId,
        IReadOnlyList<string> requiredScopes,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IServerAuthRepository>();
        var protector = scope.ServiceProvider.GetRequiredService<ITokenProtector>();

        var synced = (await repository.ListSyncedAsync(cancellationToken))
            .FirstOrDefault(c => c.EsiCharacterId == characterId);
        if (synced is null)
            return EsiAuthorization.AuthRequired;

        foreach (var requiredScope in requiredScopes)
            if (!synced.GrantedScopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase))
                return EsiAuthorization.ScopeMissing(requiredScope);

        if (_accessCache.TryGetValue(characterId, out var cached) &&
            cached.ExpiresAt - DateTimeOffset.UtcNow > RefreshSkew)
            return EsiAuthorization.Authorized(cached.AccessToken);

        try
        {
            var refreshToken = protector.Unprotect(
                new EncryptedToken(synced.RefreshTokenCipher, synced.RefreshTokenNonce, synced.RefreshTokenTag));

            var tokens = await authClient
                .RefreshAsync(refreshToken, esiOptions.ClientId, esiOptions.ClientSecret, cancellationToken);

            _accessCache[characterId] = tokens;

            // Persist the rotated refresh token so the next mint survives a restart.
            if (!string.IsNullOrEmpty(tokens.RefreshToken))
                await repository.UpsertSyncedAsync(
                    synced.EsiCharacterId, synced.CharacterName, protector.Protect(tokens.RefreshToken),
                    synced.GrantedScopes, cancellationToken);

            return EsiAuthorization.Authorized(tokens.AccessToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mint an ESI access token for synced character {Id}.", characterId);
            return EsiAuthorization.AuthRequired;
        }
    }
}
