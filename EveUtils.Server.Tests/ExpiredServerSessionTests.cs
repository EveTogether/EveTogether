using EveUtils.Server.Auth;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The state behind ET-77: a client that has been up for over an hour holds an access token the server no longer
/// accepts, while its refresh token is still perfectly good. Every unary RPC re-validates on each call, so in that
/// state saving a fleet composition (or sharing a fit) comes back "Not authenticated — pair with the server first."
/// even though the bus stream — validated ONCE, at attach — keeps delivering fleets and fits.
///
/// This pins both halves of what the client's 30s heartbeat now relies on: the expired token really does validate as
/// nothing (so <c>Session.Heartbeat</c> answers Ok=false), and the session it belongs to can still be refreshed
/// silently (so the client can heal itself instead of asking the user to re-pair).
///
/// ET-123 added the third window that decides when a session stops being a credential: silence. None of it is
/// visible from outside — a machine that was off for a week and one that is never coming back look identical in the
/// table, and getting the line wrong either leaves year-long credentials lying around or throws someone's second PC
/// off the server.
/// </summary>
public class ExpiredServerSessionTests
{
    private readonly SqliteServerDbContextFactory _factory = new();

    [Fact]
    public async Task AnExpiredAccessToken_ValidatesAsNothing_ButItsSessionStillRefreshesSilently()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new ServerAuthRepository(_factory);
        var character = await repository.UpsertSyncedAsync(90250177, "Jithran", new EncryptedToken([1], [2], [3]), null, ct);
        var sessions = new ServerSessionService(repository, NullLogger<ServerSessionService>.Instance);

        var issued = await sessions.IssueAsync(character.Id, ct);
        await BackdateAsync(repository, issued, ct);

        // What the server tells every unary RPC once the hour is up.
        Assert.Null(await sessions.ValidateAsync(issued.AccessToken, ct));

        // …and what the client can do about it without troubling the user.
        var refreshed = await sessions.RefreshAsync(issued.RefreshToken, cancellationToken: ct);
        Assert.NotNull(refreshed);
        Assert.NotNull(await sessions.ValidateAsync(refreshed!.AccessToken, ct));
    }

    [Fact]
    public async Task AnExpiredRefreshToken_IsRefusedOutright_SoTheClientMustRePair()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new ServerAuthRepository(_factory);
        var character = await repository.UpsertSyncedAsync(90250177, "Jithran", new EncryptedToken([1], [2], [3]), null, ct);
        var sessions = new ServerSessionService(repository, NullLogger<ServerSessionService>.Instance);

        var issued = await sessions.IssueAsync(character.Id, ct);
        await BackdateAsync(repository, issued, ct, refreshExpiresIn: TimeSpan.FromHours(-1));

        // The refusal (not a transport error) is what turns the character's server chip red instead of amber.
        Assert.Null(await sessions.RefreshAsync(issued.RefreshToken, cancellationToken: ct));
    }

    /// <summary>
    /// ET-123. An abandoned session used to stay a working credential for the full 365-day refresh window, and
    /// re-pairing never withdrew the one it replaced — three of those piled up on production in a single week, on
    /// top of eight left over from June. Cleaning up on "a newer session exists" was ruled out: the operator runs
    /// the same characters on three machines, and each of those is a session that has to keep working.
    ///
    /// So silence is the only ground, and this is the line itself. Anything attached is at most 30s stale
    /// (<c>ServerConnection.HeartbeatInterval</c>), so the number is not about detection — it is about how long a
    /// machine may be switched off. Both gates are checked here: the refresh, which is what a returning client
    /// actually hits, and the sweep that empties the table.
    /// </summary>
    [Theory]
    [InlineData(0, true)]       // just paired, not one heartbeat yet — LastHeartbeat == IssuedAt is no ground at all
    [InlineData(7, true)]       // the machine was off for a week: the ordinary case the window exists to survive
    [InlineData(30, true)]      // a month away
    [InlineData(61, false)]     // just past the window
    [InlineData(400, false)]    // the June leftovers measured on production
    public async Task WhetherASilentSessionIsStillACredential_IsDecidedByTheIdleWindowAlone(double silentDays, bool survives)
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new ServerAuthRepository(_factory);
        var character = await repository.UpsertSyncedAsync(90250177, "Jithran", new EncryptedToken([1], [2], [3]), null, ct);
        var sessions = new ServerSessionService(repository, NullLogger<ServerSessionService>.Instance);

        var issued = await sessions.IssueAsync(character.Id, ct);
        await BackdateAsync(repository, issued, ct, silentFor: TimeSpan.FromDays(silentDays));

        // The refresh gate first, while the row is certainly still there — so a refusal here is the guard itself.
        Assert.Equal(survives, await sessions.RefreshAsync(issued.RefreshToken, cancellationToken: ct) is not null);

        // Then the sweep, against the row the refresh left behind. Both gates have to agree, or a session is
        // refused while its row lingers as something a backup archive still hands out.
        var now = DateTimeOffset.UtcNow;
        var removed = await repository.DeleteLapsedSessionsAsync(now, now - ServerSessionService.IdleLifetime, ct);
        Assert.Equal(survives ? 0 : 1, removed);
    }

    /// <summary>
    /// The constraint the whole design is shaped by: several machines may hold a live session for one character at
    /// the same time. Pairing a new one withdraws nothing, and a sweep takes only the machine that stopped checking
    /// in — anything else would throw someone's second PC off the server.
    /// </summary>
    [Fact]
    public async Task SeveralMachinesKeepTheirOwnSession_AndOnlyTheSilentOneIsSweptUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new ServerAuthRepository(_factory);
        var character = await repository.UpsertSyncedAsync(90250177, "Jithran", new EncryptedToken([1], [2], [3]), null, ct);
        var sessions = new ServerSessionService(repository, NullLogger<ServerSessionService>.Instance);

        var desktop = await sessions.IssueAsync(character.Id, ct);
        var laptop = await sessions.IssueAsync(character.Id, ct);
        var retired = await sessions.IssueAsync(character.Id, ct);
        await BackdateAsync(repository, retired, ct, silentFor: ServerSessionService.IdleLifetime + TimeSpan.FromDays(1));

        // A third machine pairs while the first two are attached.
        var thirdPc = await sessions.IssueAsync(character.Id, ct);

        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, await repository.DeleteLapsedSessionsAsync(now, now - ServerSessionService.IdleLifetime, ct));

        Assert.NotNull(await sessions.ValidateAsync(desktop.AccessToken, ct));
        Assert.NotNull(await sessions.ValidateAsync(laptop.AccessToken, ct));
        Assert.NotNull(await sessions.ValidateAsync(thirdPc.AccessToken, ct));
        Assert.Null(await sessions.ValidateAsync(retired.AccessToken, ct));
    }

    /// <summary>Backdates a session's window without touching its tokens: <paramref name="silentFor"/> ago it was
    /// last issued or rotated and — because a rotation stamps <c>LastHeartbeat</c> too — last heard from. Two hours
    /// of that is exactly what an hour of uptime does to a client whose bus stream never dropped; sixty days of it
    /// is a machine that never came back.</summary>
    private static async Task BackdateAsync(
        ServerAuthRepository repository, IssuedSession issued, CancellationToken cancellationToken,
        TimeSpan? silentFor = null, TimeSpan? refreshExpiresIn = null)
    {
        var now = DateTimeOffset.UtcNow;
        var lastSeen = now - (silentFor ?? TimeSpan.FromHours(2));
        var session = await repository.FindSessionByAccessHashAsync(TokenSecurity.Hash(issued.AccessToken), cancellationToken);
        Assert.NotNull(session);
        Assert.True(await repository.RotateSessionAsync(
            session!.Id,
            TokenSecurity.Hash(issued.RefreshToken),   // rotating onto itself: the tokens stay, only the window moves
            TokenSecurity.Hash(issued.AccessToken),
            TokenSecurity.Hash(issued.RefreshToken),
            lastSeen, lastSeen.AddHours(1), now + (refreshExpiresIn ?? TimeSpan.FromDays(300)),
            cancellationToken));
    }
}
