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

    // How long a session may go without a single sign of life before it counts as abandoned. The client
    // heartbeats every 30s for as long as it runs (ServerConnection.HeartbeatInterval) and a rotation stamps
    // LastHeartbeat too, so a row silent for this long belongs to a machine that is never coming back with it.
    //
    // Deliberately NOT "a newer session exists for this character": one character may be paired on several
    // machines at once and every one of those is a session that has to keep working, so abandonment is the only
    // ground we may clean up on (ET-123).
    //
    // The margin is the whole of the choice. Anything alive is at most 30s stale, so telling live from not-live
    // is trivial; what the number decides is how long a machine may be switched off. A week away is ordinary and
    // a month-long holiday is not rare, so 60 days sits ~8x over the first and ~2x over the second — while
    // cutting an abandoned row's life as a usable credential from RefreshLifetime (a year) down to two months.
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(60);

    public async Task<IssuedSession> IssueAsync(int syncedCharacterId, CancellationToken cancellationToken = default)
    {
        var access = TokenSecurity.GenerateToken();
        var refresh = TokenSecurity.GenerateToken();
        var now = DateTimeOffset.UtcNow;

        var session = new ServerSession
        {
            SyncedCharacterId = syncedCharacterId,
            AccessTokenHash = TokenSecurity.Hash(access),
            RefreshTokenHash = TokenSecurity.Hash(refresh),
            IssuedAt = now,
            ExpiresAt = now + AccessLifetime,
            RefreshExpiresAt = now + RefreshLifetime,
            LastHeartbeat = now
        };
        await repository.AddSessionAsync(session, cancellationToken);

        // The insert filled in the key; hand it out so the client can name this session back to us later.
        return new IssuedSession(access, refresh, session.Id);
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

    /// <summary>
    /// Rotates the session behind <paramref name="refreshToken"/>, or says why it would not.
    /// <para><paramref name="claimedSessionId"/> is the session the client believes it holds (0 = it does not know).
    /// It is what makes a refusal answerable: the token itself cannot distinguish a session row that was deleted —
    /// swept as abandoned, revoked from the panel — from one that rotated while this client failed to persist the
    /// new pair, because in both cases the presented token is simply absent from the table. The id survives
    /// rotation, so looking it up separates the two exactly (ET-123). Without it we answer
    /// <see cref="SessionRefusalReason.Retry"/>, which is ET-121's behaviour and never sends anyone to re-pair on
    /// a guess.</para>
    /// </summary>
    public async Task<SessionRefreshResult> RefreshAsync(
        string refreshToken, string? peer = null, int claimedSessionId = 0, CancellationToken cancellationToken = default)
    {
        var presentedHash = TokenSecurity.Hash(refreshToken);
        var caller = peer ?? "an unknown peer";
        var session = await repository.FindSessionByRefreshHashAsync(presentedHash, cancellationToken);
        if (session is null)
            return await ClassifyUnknownTokenAsync(caller, presentedHash, claimedSessionId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        // Past the hard refresh window → no silent refresh; a re-pair is required.
        if (session.RefreshExpiresAt <= now)
        {
            logger.LogWarning(
                "Session.Refresh refused for {Peer}: session {SessionId} for character {Character} was last used on {LastUsed:O} and its refresh window lapsed at {RefreshExpiresAt:O}. This one really does need a re-pair.",
                caller, session.Id, Describe(session), session.IssuedAt, session.RefreshExpiresAt);
            return SessionRefreshResult.Refused(SessionRefusalReason.SessionGone);
        }

        // Silent for longer than a machine is plausibly switched off: a leftover row from an earlier pairing,
        // which stayed a working credential for a full year before ET-123. Refusing it here rather than leaving
        // it to the sweep is what makes the idle window a guarantee instead of a schedule.
        if (session.LastHeartbeat + IdleLifetime <= now)
        {
            logger.LogWarning(
                "Session.Refresh refused for {Peer}: session {SessionId} for character {Character} has shown no sign of life since {LastHeartbeat:O}, past the {IdleDays:0}-day idle window, so it counts as abandoned. This machine has to pair again.",
                caller, session.Id, Describe(session), session.LastHeartbeat, IdleLifetime.TotalDays);
            return SessionRefreshResult.Refused(SessionRefusalReason.SessionGone);
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
            return SessionRefreshResult.Ok(new IssuedSession(access, refresh, session.Id));

        logger.LogWarning(
            "Session.Refresh refused for {Peer}: session {SessionId} for character {Character} was rotated by another call while this one was in flight, so refresh token {Fingerprint} lost the race. The tokens minted here were discarded; the winner's pair stands.",
            caller, session.Id, Describe(session), Fingerprint(presentedHash));
        // The session is very much alive — the winner is holding it — so this is the one refusal a client should
        // sit out rather than treat as the end of its pairing.
        return SessionRefreshResult.Refused(SessionRefusalReason.Retry);
    }

    /// <summary>
    /// The presented refresh token is in no session at all. That single fact covers two very different situations,
    /// and the client's next move differs completely between them, so this is where they get told apart: the
    /// claimed session id outlives rotation, so if the row is still there the client's copy is merely stale, and if
    /// it is not, the session was deleted and no amount of retrying brings it back.
    /// </summary>
    private async Task<SessionRefreshResult> ClassifyUnknownTokenAsync(
        string caller, string presentedHash, int claimedSessionId, CancellationToken cancellationToken)
    {
        if (claimedSessionId <= 0)
        {
            // A client paired before it could name its session. Ambiguous, and the safe reading is the forgiving
            // one: telling someone to re-pair when their session is fine is the regression ET-121 fixed.
            logger.LogWarning(
                "Session.Refresh refused for {Peer}: refresh token {Fingerprint} is not a known session, and the caller named no session id, so this cannot be told from a rotation it failed to persist. Treated as retryable.",
                caller, Fingerprint(presentedHash));
            return SessionRefreshResult.Refused(SessionRefusalReason.Retry);
        }

        var claimed = await repository.FindSessionByIdAsync(claimedSessionId, cancellationToken);
        if (claimed is null)
        {
            logger.LogWarning(
                "Session.Refresh refused for {Peer}: session {SessionId} does not exist here any more — swept as abandoned, revoked, or decoupled — so refresh token {Fingerprint} can never work again. This machine has to couple the character afresh.",
                caller, claimedSessionId, Fingerprint(presentedHash));
            return SessionRefreshResult.Refused(SessionRefusalReason.SessionGone);
        }

        logger.LogWarning(
            "Session.Refresh refused for {Peer}: session {SessionId} for character {Character} is still here but has rotated past refresh token {Fingerprint}, so this client is holding a copy it never replaced. Retryable — the pairing stands.",
            caller, claimed.Id, Describe(claimed), Fingerprint(presentedHash));
        return SessionRefreshResult.Refused(SessionRefusalReason.Retry);
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
