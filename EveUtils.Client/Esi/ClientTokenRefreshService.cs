using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Esi;

/// <summary>
/// Background service that refreshes per-character ESI tokens before they expire.
/// Runs every 60 s; refreshes any token whose remaining lifetime is under 5 minutes.
/// Publishes <c>TokenRefreshedEvent</c> / <c>TokenRefreshFailedEvent</c> on the
/// local event bus so the UI and other services stay in sync.
/// </summary>
public sealed class ClientTokenRefreshService(
    ICharacterRegistry registry,
    IPerCharacterTokenStore tokenStore,
    IEsiAuthClient authClient,
    IEsiJwtValidator jwtValidator,
    EsiOptions options,
    ILogger<ClientTokenRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshThreshold = TimeSpan.FromMinutes(5);
    // After a refresh yields an unusable token (validation fails — almost always clock skew), wait this long before
    // trying again. Without it every 5s ESI consumer would re-refresh against EVE SSO and re-log on every tick.
    private static readonly TimeSpan UnusableBackoff = TimeSpan.FromSeconds(60);
    private readonly Dictionary<int, DateTimeOffset> _unusableRetryAfter = new(); // per-char back-off after an unusable refresh

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);

                var characters = await registry.GetAllAsync(stoppingToken);
                foreach (var character in characters)
                {
                    if (character.EsiCharacterId is not { } charId) continue;

                    try
                    {
                        await EnsureValidAsync(charId, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Unexpected error refreshing token for character {CharacterId}.", charId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error during client token refresh cycle.");
            }
        }
    }

    /// <summary>
    /// Checks the character's ESI token and refreshes it if expiring. Returns the outcome
    /// so the caller (e.g. the startup check) can surface a "re-auth needed" indication.
    /// </summary>
    public async Task<TokenStatus> EnsureValidAsync(int charId, CancellationToken cancellationToken = default)
    {
        var tokens = await tokenStore.LoadAsync(charId, cancellationToken);
        if (tokens is null) return TokenStatus.NoToken;

        var remaining = tokens.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining > RefreshThreshold)
        {
            _unusableRetryAfter.Remove(charId); // a valid token ends any unusable run
            return TokenStatus.Valid;
        }

        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            logger.LogError("Character {CharacterId} has no refresh token; re-auth needed.", charId);
            return TokenStatus.NeedsReauth;
        }

        // While backing off from an unusable refresh, skip the SSO round-trip and report unavailable — so the 5s ESI
        // consumers don't re-refresh + re-log every tick during a clock-skew window.
        if (_unusableRetryAfter.TryGetValue(charId, out var retryAfter) && DateTimeOffset.UtcNow < retryAfter)
            return TokenStatus.TemporarilyUnavailable;

        var character = (await registry.GetAllAsync(cancellationToken))
            .FirstOrDefault(c => c.EsiCharacterId == charId);

        try
        {
            var refreshed = await authClient
                .RefreshAsync(tokens.RefreshToken, options.ClientId, options.ClientSecret, cancellationToken);

            var identity = await jwtValidator
                .ValidateAsync(refreshed.AccessToken, options.ClientId, cancellationToken);

            await tokenStore.SaveAsync(charId, refreshed, cancellationToken);

            if (character is not null)
                await registry.AddOrUpdateAsync(character with { GrantedScopes = identity.GrantedScopes }, cancellationToken);

            _unusableRetryAfter.Remove(charId); // recovered
            logger.LogInformation("Token refreshed for character {CharacterId}.", charId);
            return TokenStatus.Refreshed;
        }
        catch (Exception ex) when (IsRevoked(ex))
        {
            logger.LogError(ex, "Token revoked for character {CharacterId} — re-auth needed.", charId);
            return TokenStatus.NeedsReauth;
        }
        catch (Exception ex)
        {
            // Refresh succeeded at the HTTP level but the token is unusable (it fails validation — almost always a
            // local clock skew vs EVE's token lifetime). Re-auth won't fix it and retrying every cycle would spam SSO
            // and the log, so back off and surface it as transient. Log it once per outage at Warning (the first
            // failure of a run), then quietly at Debug until a good refresh clears the back-off.
            var firstOfRun = !_unusableRetryAfter.ContainsKey(charId);
            _unusableRetryAfter[charId] = DateTimeOffset.UtcNow + UnusableBackoff;
            if (firstOfRun)
                logger.LogWarning(ex, "ESI token for character {CharacterId} was refreshed but failed validation — " +
                    "treating it as temporarily unavailable (often a local clock skew vs the token lifetime). " +
                    "Backing off for {Backoff} before retrying.", charId, UnusableBackoff);
            else
                logger.LogDebug(ex, "ESI token for character {CharacterId} still failing validation; backing off.", charId);
            return TokenStatus.TemporarilyUnavailable;
        }
    }

    private static bool IsRevoked(Exception ex) =>
        ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
}
