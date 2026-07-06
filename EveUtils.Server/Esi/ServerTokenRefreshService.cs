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
    ILogger<ServerTokenRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
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

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IServerAuthRepository>();
        var protector = scope.ServiceProvider.GetRequiredService<ITokenProtector>();

        var synced = await repository.ListSyncedAsync(cancellationToken);
        foreach (var character in synced)
        {
            if (IsDevSeed(character, protector)) continue;
            if (ShouldRefresh(character))
                await TryRefreshAsync(character, repository, protector, cancellationToken);
        }
    }

    private async Task TryRefreshAsync(
        SyncedCharacter character,
        IServerAuthRepository repository,
        ITokenProtector protector,
        CancellationToken cancellationToken)
    {
        try
        {
            var encrypted = new EncryptedToken(character.RefreshTokenCipher, character.RefreshTokenNonce, character.RefreshTokenTag);
            var refreshToken = protector.Unprotect(encrypted);

            var tokens = await authClient
                .RefreshAsync(refreshToken, esiOptions.ClientId, esiOptions.ClientSecret, cancellationToken);

            var identity = await jwtValidator
                .ValidateAsync(tokens.AccessToken, esiOptions.ClientId, cancellationToken);

            var newEncrypted = protector.Protect(tokens.RefreshToken ?? refreshToken);
            await repository.UpsertSyncedAsync(
                character.EsiCharacterId,
                character.CharacterName,
                newEncrypted,
                identity.GrantedScopes,
                cancellationToken);

            logger.LogInformation(
                "Server token refreshed for {Name} ({Id}).",
                character.CharacterName, character.EsiCharacterId);
        }
        catch (Exception ex) when (IsRevoked(ex))
        {
            logger.LogError(ex,
                "Token revoked for synced character {Name} ({Id}). Marking as expired.",
                character.CharacterName, character.EsiCharacterId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to refresh token for synced character {Name} ({Id}).",
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

    private static bool ShouldRefresh(SyncedCharacter character) =>
        character.LastRefreshedAt is null || DateTimeOffset.UtcNow - character.LastRefreshedAt.Value > RefreshAfter;

    private static bool IsRevoked(Exception ex) =>
        ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
}
