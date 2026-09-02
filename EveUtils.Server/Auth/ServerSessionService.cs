using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.ServerAuth.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Auth;

/// <summary>
/// Issues + validates the server's own session tokens, separate from the EVE tokens. Tokens
/// are stored hashed; reconnect is a silent refresh. Used by the pairing flow, the
/// Session service and the auth-gated event bus.
/// </summary>
public sealed class ServerSessionService(IServerAuthRepository repository, ILogger<ServerSessionService> logger) : IScopedService
{
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(1);

    // Hard session lifetime, decoupled from the 1h access window: the refresh token (and its row)
    // survives this long so a silent reconnect keeps working. It SLIDES forward on every refresh
    // (RotateSessionAsync), so an actively-used client never re-pairs; the window only bites after this many
    // days of zero use. Trusted local TOFU-pinned client → keep it long so re-login is rare.
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(365);

    public async Task<IssuedSession> IssueAsync(int syncedCharacterId, CancellationToken cancellationToken = default)
    {
        var access = TokenSecurity.GenerateToken();
        var refresh = TokenSecurity.GenerateToken();
        var now = DateTimeOffset.UtcNow;

        await repository.AddSessionAsync(new ServerSession
        {
            SyncedCharacterId = syncedCharacterId,
            AccessTokenHash = TokenSecurity.Hash(access),
            RefreshTokenHash = TokenSecurity.Hash(refresh),
            IssuedAt = now,
            ExpiresAt = now + AccessLifetime,
            RefreshExpiresAt = now + RefreshLifetime,
            LastHeartbeat = now
        }, cancellationToken);

        return new IssuedSession(access, refresh);
    }

    public async Task<ServerSession?> ValidateAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var session = await repository.FindSessionByAccessHashAsync(TokenSecurity.Hash(accessToken), cancellationToken);
        if (session is null) return null;

        var now = DateTimeOffset.UtcNow;
        if (session.ExpiresAt > now) return session;

        // Access token expired (1h). Do NOT delete the row here: the refresh token must survive the full
        // 30-day window so a client that has been away for hours/days can silently Session.Refresh.
        // Deleting on access-expiry destroyed the refresh token and forced a re-pair. On-encounter
        // cleanup only drops the session once the hard refresh window has also lapsed; otherwise the
        // background ServerSessionCleanupService purges on RefreshExpiresAt.
        if (session.RefreshExpiresAt <= now)
            await repository.DeleteSessionAsync(session.Id, cancellationToken);
        return null;
    }

    public Task TouchAsync(string accessToken, CancellationToken cancellationToken = default) =>
        repository.TouchHeartbeatAsync(TokenSecurity.Hash(accessToken), DateTimeOffset.UtcNow, cancellationToken);

    public async Task<IssuedSession?> RefreshAsync(string refreshToken, string? peer = null, CancellationToken cancellationToken = default)
    {
        var presentedHash = TokenSecurity.Hash(refreshToken);
        var caller = peer ?? "an unknown peer";
        var session = await repository.FindSessionByRefreshHashAsync(presentedHash, cancellationToken);
        if (session is null)
        {
            // The token is not in the table at all: either revoked, or a copy that a later rotation superseded.
            // ET-121 could not tell those apart afterwards because a refused refresh left no trace at all.
            logger.LogWarning(
                "Session.Refresh refused for {Peer}: refresh token {Fingerprint} is not a known session. It was revoked, or a newer rotation replaced it and this client kept an outdated copy.",
                caller, Fingerprint(presentedHash));
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        // Past the hard refresh window → no silent refresh; a re-pair is required.
        if (session.RefreshExpiresAt <= now)
        {
            logger.LogWarning(
                "Session.Refresh refused for {Peer}: session {SessionId} for character {Character} was last used on {LastUsed:O} and its refresh window lapsed at {RefreshExpiresAt:O}. This one really does need a re-pair.",
                caller, session.Id, Describe(session), session.IssuedAt, session.RefreshExpiresAt);
            return null;
        }

        var access = TokenSecurity.GenerateToken();
        var refresh = TokenSecurity.GenerateToken();

        // Rotate the access token (1h) and slide the refresh window forward so an active session keeps
        // reconnecting silently; an idle one eventually lapses after RefreshLifetime. The rotation is conditional
        // on the refresh hash we just read, so of two overlapping refreshes exactly one wins.
        var rotated = await repository.RotateSessionAsync(
            session.Id, presentedHash, TokenSecurity.Hash(access), TokenSecurity.Hash(refresh),
            now, now + AccessLifetime, now + RefreshLifetime, cancellationToken);
        if (rotated)
            return new IssuedSession(access, refresh);

        logger.LogWarning(
            "Session.Refresh refused for {Peer}: session {SessionId} for character {Character} was rotated by another call while this one was in flight, so refresh token {Fingerprint} lost the race. The tokens minted here were discarded; the winner's pair stands.",
            caller, session.Id, Describe(session), Fingerprint(presentedHash));
        return null;
    }

    /// <summary>
    /// Client-initiated decouple: delete the session bound to this access token so it can no
    /// longer be used to attach. Returns true if a session was found and removed.
    /// </summary>
    public async Task<bool> RevokeAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var session = await repository.FindSessionByAccessHashAsync(TokenSecurity.Hash(accessToken), cancellationToken);
        if (session is null)
            return false;

        await repository.DeleteSessionAsync(session.Id, cancellationToken);
        return true;
    }

    /// <summary>Enough of the stored hash to line two log lines up against each other, far too little to be a token.</summary>
    private static string Fingerprint(string hash) => hash.Length <= 8 ? hash : hash[..8];

    private static string Describe(ServerSession session) =>
        session.SyncedCharacter is null
            ? $"#{session.SyncedCharacterId}"
            : $"{session.SyncedCharacter.CharacterName} (#{session.SyncedCharacterId})";
}
