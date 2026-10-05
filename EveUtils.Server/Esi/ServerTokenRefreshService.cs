using EveUtils.Server.Auth;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Esi;

/// <summary>
/// Background service that refreshes server-side ESI tokens for all synced characters before they
/// expire. Runs every 60 s; refreshes tokens if the character was last refreshed > 15 minutes
/// ago (EVE tokens expire in ~20 minutes). Decrypt → refresh → encrypt → upsert.
/// </summary>
public sealed class ServerTokenRefreshService(
    IServiceScopeFactory scopeFactory,
    IEsiAuthClient authClient,
    IEsiJwtValidator jwtValidator,
    EsiOptions esiOptions,
    TimeProvider time,
    ILogger<ServerTokenRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(15);
    // A revoked grant cannot recover without re-pairing, so it bypasses the transient retry schedule.
    internal const int RevokedFailureCount = int.MaxValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, time, stoppingToken);
                await RefreshAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error during server token refresh cycle.");
            }
        }
    }

    internal async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IServerAuthRepository>();
        var protector = scope.ServiceProvider.GetRequiredService<ITokenProtector>();

        var releaser = scope.ServiceProvider.GetRequiredService<SyncedCharacterReleaser>();

        // A character without a session has nobody left to serve; keeping its token fresh is what kept a decoupled
        // player's grant alive on the server (ET-344).
        var synced = await repository.ListSyncedWithSessionsAsync(cancellationToken);
        foreach (var character in synced)
        {
            if (IsDevSeed(character, protector)) continue;
            if (ShouldRefresh(character))
                await TryRefreshAsync(character, repository, protector, releaser, cancellationToken);
        }
    }

    private async Task TryRefreshAsync(
        SyncedCharacter character,
        IServerAuthRepository repository,
        ITokenProtector protector,
        SyncedCharacterReleaser releaser,
        CancellationToken cancellationToken)
    {
        try
        {
            var encrypted = new EncryptedToken(character.RefreshTokenCipher, character.RefreshTokenNonce, character.RefreshTokenTag);
            var refreshToken = protector.Unprotect(encrypted);

            var tokens = await authClient
                .RefreshAsync(refreshToken, esiOptions.ClientId, esiOptions.ClientSecret, cancellationToken);
            var latestRefreshToken = tokens.RefreshToken ?? refreshToken;

            // EVE SSO rotates refresh tokens: the one just sent is spent. Keep the new one before validation, which
            // fetches the signing keys over the network — a hiccup there used to lose it, and the retry then offered
            // the spent token and got invalid_grant, which ends the grant for good (ET-446, as ET-445 on the client).
            var stored = latestRefreshToken == refreshToken
                         || await repository.UpdateSyncedRefreshTokenAsync(
                             character.EsiCharacterId, protector.Protect(latestRefreshToken), cancellationToken);
            if (stored)
            {
                var identity = await jwtValidator
                    .ValidateAsync(tokens.AccessToken, esiOptions.ClientId, cancellationToken);

                stored = await repository.UpdateSyncedTokenAsync(
                    character.EsiCharacterId,
                    character.CharacterName,
                    protector.Protect(latestRefreshToken),
                    identity.GrantedScopes,
                    cancellationToken);
            }

            if (!stored)
            {
                // Released while this refresh was in flight. Writing back would bring the row and its token back, and
                // CCP may have handed out a new refresh token that nothing holds a record of any more.
                logger.LogInformation(
                    "Synced character {Name} ({Id}) was released during its token refresh; nothing was stored.",
                    character.CharacterName, character.EsiCharacterId);
                await releaser.RevokeAtCcpAsync(latestRefreshToken, character.CharacterName, character.EsiCharacterId, cancellationToken);
                return;
            }

            logger.LogInformation(
                "Server token refreshed for {Name} ({Id}).",
                character.CharacterName, character.EsiCharacterId);
        }
        catch (Exception ex) when (IsRevoked(ex))
        {
            await RecordFailureAsync(character, repository, RevokedFailureCount, cancellationToken);
            logger.LogError(ex,
                "Token revoked for synced character {Name} ({Id}). Refresh stopped until re-paired.",
                character.CharacterName, character.EsiCharacterId);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(character, repository, character.FailureCount + 1, cancellationToken);
            logger.LogError(ex,
                "Failed to refresh token for synced character {Name} ({Id}). Backing off.",
                character.CharacterName, character.EsiCharacterId);
        }
    }

    // Development seeds inject a placeholder refresh token ("dev-refresh"); refreshing it against the real
    // ESI endpoint only produces invalid_grant "Unable to migrate grant" spam, so skip those characters.
    private static bool IsDevSeed(SyncedCharacter character, ITokenProtector protector)
    {
        try
        {
            var encrypted = new EncryptedToken(character.RefreshTokenCipher, character.RefreshTokenNonce, character.RefreshTokenTag);
            return string.Equals(protector.Unprotect(encrypted), "dev-refresh", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private async Task RecordFailureAsync(SyncedCharacter character, IServerAuthRepository repository, int failureCount, CancellationToken cancellationToken)
    {
        var failedAt = time.GetUtcNow();
        character.LastFailedAt = failedAt;
        character.FailureCount = failureCount;
        await repository.RecordRefreshFailureAsync(character.EsiCharacterId, failedAt, failureCount, cancellationToken);
    }

    private bool ShouldRefresh(SyncedCharacter character)
    {
        if (character.FailureCount == RevokedFailureCount) return false;
        if (character.LastFailedAt is not null && time.GetUtcNow() - character.LastFailedAt.Value < FailureBackoff(character.EsiCharacterId, character.FailureCount)) return false;
        return character.LastRefreshedAt is null || time.GetUtcNow() - character.LastRefreshedAt.Value > RefreshAfter;
    }

    /// <summary>5, 10, 20, 40 minutes, then hourly, with ±10% jitter so characters that failed together do not retry
    /// in lockstep. The jitter is fixed per character and attempt: the 60 s loop asks again every pass, and a fresh
    /// roll each time would only pull the retry towards the short end.</summary>
    internal static TimeSpan FailureBackoff(int esiCharacterId, int failureCount)
    {
        var minutes = Math.Min(5 * Math.Pow(2, Math.Clamp(failureCount, 1, 8) - 1), 60);
        var jitter = 0.9 + new Random(HashCode.Combine(esiCharacterId, failureCount)).NextDouble() * 0.2;
        return TimeSpan.FromMinutes(minutes * jitter);
    }

    // Only the SSO's own verdict on the grant. Matching "401" anywhere in the message took an HTML 401 from a proxy,
    // or invalid_client, for a revoked grant and stopped refreshing until the player re-paired.
    internal static bool IsRevoked(Exception ex) => ex is EsiTokenExchangeException exchange
        ? exchange.IsDefinitiveRejection
        : ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
}
