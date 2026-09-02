using System.Collections.Concurrent;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Esi;

/// <summary>
/// Background service that refreshes per-character ESI tokens before they expire.
/// Runs every 60 s; refreshes any token whose remaining lifetime is under 5 minutes.
/// Every outcome is recorded on <see cref="EsiTokenStatusTracker"/>, which publishes
/// <see cref="Shared.Modules.Esi.Events.TokenRefreshedEvent"/> /
/// <see cref="Shared.Modules.Esi.Events.TokenRefreshFailedEvent"/> on the local event bus so the UI
/// and other services stay in sync.
/// </summary>
public sealed class ClientTokenRefreshService(
    ICharacterRegistry registry,
    IPerCharacterTokenStore tokenStore,
    IEsiAuthClient authClient,
    IEsiJwtValidator jwtValidator,
    EsiOptions options,
    EsiTokenStatusTracker statusTracker,
    ILogger<ClientTokenRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshThreshold = TimeSpan.FromMinutes(5);
    // After a refresh yields an unusable token (validation fails — almost always clock skew), wait this long before
    // trying again. Without it every 5s ESI consumer would re-refresh against EVE SSO and re-log on every tick.
    private static readonly TimeSpan UnusableBackoff = TimeSpan.FromSeconds(60);
    // Concurrent: EnsureValidAsync is reached from the 60 s loop and from every ESI call, on any thread.
    private readonly ConcurrentDictionary<int, DateTimeOffset> _unusableRetryAfter = new(); // per-char back-off after an unusable refresh
    // One gate per character so two callers never send the same refresh token to EVE SSO at the same time —
    // with a rotating refresh token the loser of that race gets invalid_grant and the account is really signed out.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();
    // Characters whose stored token ESI has just refused: the next check refreshes instead of trusting the clock.
    private readonly ConcurrentDictionary<int, bool> _refused = new();
    // Floor under those forced refreshes. Without it a token ESI keeps refusing would mean one SSO round-trip per
    // ESI call for as long as it lasts; with it the character simply stays Rejected until the cooldown lapses.
    private static readonly TimeSpan ForcedRefreshCooldown = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<int, DateTimeOffset> _forcedRefreshAfter = new();

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
    /// Checks the character's ESI token and refreshes it if expiring. Returns the outcome, and records it on
    /// <see cref="EsiTokenStatusTracker"/> so the character list shows this account's own state without the
    /// caller having to push it anywhere.
    /// Serialized per character: the 60 s loop and every ESI call reach this, and two concurrent refreshes of
    /// one character would race on the same refresh token.
    /// </summary>
    public async Task<TokenStatus> EnsureValidAsync(int charId, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(charId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        TokenStatus status;
        try
        {
            status = await EnsureValidCoreAsync(charId, cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        // Outside the gate on purpose: recording notifies the UI and publishes on the event bus, and a
        // subscriber must never be able to deadlock a token refresh by asking about the same character.
        await statusTracker.RecordAsync(charId, status, cancellationToken);
        return status;
    }

    /// <summary>
    /// ESI answered 401 for this character's token. Distrusts the stored token so the next check refreshes rather
    /// than believing its expiry, and puts the character on the badge as <see cref="TokenStatus.Rejected"/> — a
    /// refused token used to be invisible here, because nothing carried ESI's opinion back into the status the UI
    /// reads (ET-121).
    /// </summary>
    public async Task RecordRefusalAsync(int charId, CancellationToken cancellationToken = default)
    {
        _refused[charId] = true;
        await statusTracker.RecordAsync(charId, TokenStatus.Rejected, cancellationToken);
    }

    private async Task<TokenStatus> EnsureValidCoreAsync(int charId, CancellationToken cancellationToken)
    {
        var tokens = await tokenStore.LoadAsync(charId, cancellationToken);
        if (tokens is null) return TokenStatus.NoToken;

        var now = DateTimeOffset.UtcNow;
        // A token ESI has refused gets refreshed even though its own clock says it is still good — that clock is
        // exactly what was wrong. Honoured at most once per cooldown so a persistently refused token cannot turn
        // every ESI call into an SSO round-trip.
        var forced = _refused.TryRemove(charId, out _);
        if (forced && _forcedRefreshAfter.TryGetValue(charId, out var notBefore) && now < notBefore)
            return TokenStatus.Rejected; // refreshed for this reason moments ago and ESI still says no — hold the verdict
        if (forced)
            _forcedRefreshAfter[charId] = now + ForcedRefreshCooldown;

        var remaining = tokens.ExpiresAt - now;
        if (!forced && remaining > RefreshThreshold)
        {
            _unusableRetryAfter.TryRemove(charId, out _); // a valid token ends any unusable run
            return TokenStatus.Valid;
        }

        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            logger.LogError("Character {CharacterId} has no refresh token; re-auth needed.", charId);
            // No refresh token means the stored token set can never become valid again, unlike
            // TemporarilyUnavailable — remove it so a dead blob doesn't linger (ET-54).
            await tokenStore.RemoveAsync(charId, cancellationToken);
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

            // Only write when the grant actually changed. AddOrUpdateAsync raises RegistryChanged, and the UI
            // treats that as "the set of characters changed" and rebuilds the whole list — so writing after every
            // refresh meant the 60 s loop could rebuild the character list every minute for nothing (ET-24).
            if (character is not null && !ScopesEqual(character.GrantedScopes, identity.GrantedScopes))
                await registry.AddOrUpdateAsync(character with { GrantedScopes = identity.GrantedScopes }, cancellationToken);

            _unusableRetryAfter.TryRemove(charId, out _); // recovered
            logger.LogInformation("Token refreshed for character {CharacterId}.", charId);
            return TokenStatus.Refreshed;
        }
        catch (Exception ex) when (IsRevoked(ex))
        {
            logger.LogError(ex, "Token revoked for character {CharacterId} — re-auth needed.", charId);
            // A definitive invalid_grant/401 means the refresh token is dead for good — unlike the
            // TemporarilyUnavailable case below, retrying will never recover it, so the encrypted
            // blob is just dead weight on disk from here on (ET-54).
            await tokenStore.RemoveAsync(charId, cancellationToken);
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

    // Grant comparison is set-like: EVE returns the granted scopes in no guaranteed order, so an order
    // difference is not a change. Ordinal (invariant) — scope names are protocol identifiers, not text.
    private static bool ScopesEqual(IReadOnlyList<string>? current, IReadOnlyList<string>? granted) =>
        new HashSet<string>(current ?? [], StringComparer.OrdinalIgnoreCase).SetEquals(granted ?? []);

    private static bool IsRevoked(Exception ex) =>
        ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
}
