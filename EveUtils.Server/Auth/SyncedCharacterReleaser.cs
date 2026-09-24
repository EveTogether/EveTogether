using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Auth;

/// <summary>
/// Lets go of a paired character once no machine is coupled to it any more: the row and its encrypted EVE refresh
/// token are deleted and the token is revoked at CCP, so decoupling ends the server's hold on the grant instead of
/// leaving a token that keeps being refreshed for a player who believes they are free of the server (ET-344).
/// A failed CCP revoke is logged and never undoes the delete.
/// </summary>
public sealed class SyncedCharacterReleaser(
    IServerAuthRepository repository,
    ITokenProtector protector,
    IEsiTokenRevoker revoker,
    EsiOptions esiOptions,
    ILogger<SyncedCharacterReleaser> logger) : IScopedService
{
    /// <summary>Releases the character when it has no session left; one still coupled from another machine keeps it.</summary>
    public async Task<bool> ReleaseIfWithoutSessionAsync(int syncedCharacterId, CancellationToken cancellationToken = default)
    {
        var released = await repository.DeleteSyncedIfWithoutSessionAsync(syncedCharacterId, cancellationToken);
        if (released is null)
            return false;

        await _RevokeReleasedAsync(released, cancellationToken);
        return true;
    }

    /// <summary>Releases every character without a session — the rows left behind by decouples from before ET-344, and by
    /// sessions that were swept or deleted from the panel.</summary>
    public async Task<int> ReleaseAllWithoutSessionAsync(CancellationToken cancellationToken = default)
    {
        var released = await repository.DeleteSyncedWithoutSessionAsync(cancellationToken);
        foreach (var character in released)
            await _RevokeReleasedAsync(character, cancellationToken);
        return released.Count;
    }

    /// <summary>Revokes a refresh token at CCP, best effort: the caller has already decided the token is not to be kept.</summary>
    public async Task RevokeAtCcpAsync(string refreshToken, string characterName, int esiCharacterId, CancellationToken cancellationToken = default)
    {
        try
        {
            var clientSecret = esiOptions.ClientSecret ?? throw new InvalidOperationException("Esi:ClientSecret is not configured");
            await revoker.RevokeRefreshTokenAsync(refreshToken, esiOptions.ClientId, clientSecret, cancellationToken);
            logger.LogInformation("Revoked the server's EVE token for {Name} ({Id}) at CCP.", characterName, esiCharacterId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not revoke the server's EVE token for {Name} ({Id}) at CCP; the character is released anyway.",
                characterName, esiCharacterId);
        }
    }

    /// <summary>Decrypts the token of a character row that is already deleted and revokes it at CCP, best effort.</summary>
    public async Task RevokeStoredTokenAsync(SyncedCharacter deleted, CancellationToken cancellationToken = default)
    {
        string refreshToken;
        try
        {
            refreshToken = protector.Unprotect(new EncryptedToken(deleted.RefreshTokenCipher, deleted.RefreshTokenNonce, deleted.RefreshTokenTag));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "The stored EVE token of {Name} ({Id}) could not be decrypted, so it was deleted without a revoke at CCP.",
                deleted.CharacterName, deleted.EsiCharacterId);
            return;
        }

        await RevokeAtCcpAsync(refreshToken, deleted.CharacterName, deleted.EsiCharacterId, cancellationToken);
    }

    private async Task _RevokeReleasedAsync(SyncedCharacter released, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Released {Name} ({Id}): no session is coupled to it any more, so its stored EVE token was deleted.",
            released.CharacterName, released.EsiCharacterId);
        await RevokeStoredTokenAsync(released, cancellationToken);
    }
}
