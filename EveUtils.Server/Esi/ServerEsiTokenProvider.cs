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
    ServerTokenRefreshGate refreshGate,
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

        var synced = await repository.FindSyncedAsync(characterId, cancellationToken);
        if (synced is null)
            return EsiAuthorization.AuthRequired;

        foreach (var requiredScope in requiredScopes)
            if (!synced.GrantedScopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase))
                return EsiAuthorization.ScopeMissing(requiredScope);

        if (_CachedAccessToken(characterId) is { } cached)
            return EsiAuthorization.Authorized(cached);

        using var held = await refreshGate.EnterAsync(characterId, cancellationToken);
        // Single flight: a caller that waited here usually finds the token the one before it just minted.
        if (_CachedAccessToken(characterId) is { } minted)
            return EsiAuthorization.Authorized(minted);

        try
        {
            // Read again inside the gate: the background refresh may have rotated the refresh token meanwhile (ET-448).
            var current = await repository.FindSyncedAsync(characterId, cancellationToken);
            if (current is null)
                return EsiAuthorization.AuthRequired;

            var refreshToken = protector.Unprotect(
                new EncryptedToken(current.RefreshTokenCipher, current.RefreshTokenNonce, current.RefreshTokenTag));

            var tokens = await authClient
                .RefreshAsync(refreshToken, esiOptions.ClientId, esiOptions.ClientSecret, cancellationToken);

            // Persist the rotated refresh token so the next mint survives a restart. An update, not an upsert: a
            // character released while this was in flight must not come back.
            var latestRefreshToken = tokens.RefreshToken ?? refreshToken;
            if (latestRefreshToken != refreshToken
                && !await repository.UpdateSyncedRefreshTokenAsync(characterId, protector.Protect(latestRefreshToken), cancellationToken))
                return EsiAuthorization.AuthRequired;

            _accessCache[characterId] = tokens;
            return EsiAuthorization.Authorized(tokens.AccessToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mint an ESI access token for synced character {Id}.", characterId);
            return EsiAuthorization.AuthRequired;
        }
    }

    private string? _CachedAccessToken(int characterId) =>
        _accessCache.TryGetValue(characterId, out var cached) && cached.ExpiresAt - DateTimeOffset.UtcNow > RefreshSkew
            ? cached.AccessToken
            : null;
}
