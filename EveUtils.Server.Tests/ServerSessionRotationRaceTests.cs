using EveUtils.Server.Auth;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// ET-122. <c>RotateSessionAsync</c> used to read the row and then write it back unconditionally, so two refreshes
/// that overlapped both committed: the second overwrote the token pair the first had just handed out, and that first
/// client — holding tokens the table no longer knew — was told "invalid or expired refresh token" on its next call.
/// ET-121 measured what that costs (a character silently losing its pairing) and closed the client half with a single
/// owner per (server, character); this pins the server half, which has to hold regardless of what a client does.
///
/// The rotation is now conditional on the refresh hash the caller read, so the database picks the winner. These tests
/// drive the losing side deliberately, because two rotations both succeeding is exactly the kind of damage that
/// leaves no trace you can see afterwards.
/// </summary>
public class ServerSessionRotationRaceTests
{
    private readonly SqliteServerDbContextFactory _factory = new();

    [Fact]
    public async Task TwoRotationsFromTheSameObservedSession_OnlyTheFirstOneCommits()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new ServerAuthRepository(_factory);
        var character = await repository.UpsertSyncedAsync(90250177, "Jithran", new EncryptedToken([1], [2], [3]), null, ct);
        var sessions = new ServerSessionService(repository, NullLogger<ServerSessionService>.Instance);
        var issued = await sessions.IssueAsync(character.Id, ct);

        // Both callers read the same row and mint their own pair — the state two overlapping Session.Refresh calls
        // are in the moment before either of them writes.
        var observedRefreshHash = TokenSecurity.Hash(issued.RefreshToken);
        var session = await repository.FindSessionByRefreshHashAsync(observedRefreshHash, ct);
        Assert.NotNull(session);

        var winner = new IssuedSession(TokenSecurity.GenerateToken(), TokenSecurity.GenerateToken(), session!.Id);
        var loser = new IssuedSession(TokenSecurity.GenerateToken(), TokenSecurity.GenerateToken(), session.Id);
        var now = DateTimeOffset.UtcNow;

        Assert.True(await Rotate(repository, session.Id, observedRefreshHash, winner, now, ct));
        Assert.False(await Rotate(repository, session.Id, observedRefreshHash, loser, now, ct));

        // The row still belongs to the winner: the loser neither took it over nor left a half-written mix behind.
        Assert.NotNull(await repository.FindSessionByRefreshHashAsync(TokenSecurity.Hash(winner.RefreshToken), ct));
        Assert.Null(await repository.FindSessionByRefreshHashAsync(TokenSecurity.Hash(loser.RefreshToken), ct));
        Assert.NotNull(await repository.FindSessionByAccessHashAsync(TokenSecurity.Hash(winner.AccessToken), ct));
    }

    [Fact]
    public async Task TheSecondRefreshOfOneToken_IsRefused_AndLeavesTheFirstOnesTokensWorking()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new ServerAuthRepository(_factory);
        var character = await repository.UpsertSyncedAsync(90250177, "Jithran", new EncryptedToken([1], [2], [3]), null, ct);
        var sessions = new ServerSessionService(repository, NullLogger<ServerSessionService>.Instance);
        var issued = await sessions.IssueAsync(character.Id, ct);

        var first = (await sessions.RefreshAsync(issued.RefreshToken, cancellationToken: ct)).Issued;
        Assert.NotNull(first);

        // The overlapping caller presents the same refresh token a moment later. Refused — where it used to be
        // served, invalidating the pair the first caller is already using.
        Assert.Null((await sessions.RefreshAsync(issued.RefreshToken, cancellationToken: ct)).Issued);

        Assert.NotNull(await sessions.ValidateAsync(first!.AccessToken, ct));
        Assert.NotNull((await sessions.RefreshAsync(first.RefreshToken, cancellationToken: ct)).Issued);
    }

    /// <summary>The path everything hangs on: an ordinary reconnect refresh still rotates and keeps working.</summary>
    [Fact]
    public async Task AnOrdinaryRefresh_RotatesTheSessionAndRetiresTheOldTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new ServerAuthRepository(_factory);
        var character = await repository.UpsertSyncedAsync(90250177, "Jithran", new EncryptedToken([1], [2], [3]), null, ct);
        var sessions = new ServerSessionService(repository, NullLogger<ServerSessionService>.Instance);
        var issued = await sessions.IssueAsync(character.Id, ct);

        var refreshed = (await sessions.RefreshAsync(issued.RefreshToken, cancellationToken: ct)).Issued;
        Assert.NotNull(refreshed);
        Assert.NotEqual(issued.AccessToken, refreshed!.AccessToken);
        Assert.NotEqual(issued.RefreshToken, refreshed.RefreshToken);

        Assert.NotNull(await sessions.ValidateAsync(refreshed.AccessToken, ct));
        Assert.Null(await sessions.ValidateAsync(issued.AccessToken, ct));

        // Still one session row for this character — a refresh rotates in place, it does not pile up pairings.
        var all = await repository.ListSessionsAsync(ct);
        Assert.Single(all, s => s.SyncedCharacterId == character.Id);
    }

    private static Task<bool> Rotate(
        ServerAuthRepository repository, int sessionId, string observedRefreshHash, IssuedSession minted,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        repository.RotateSessionAsync(
            sessionId, observedRefreshHash,
            TokenSecurity.Hash(minted.AccessToken), TokenSecurity.Hash(minted.RefreshToken),
            now, now.AddHours(1), now.AddDays(365), cancellationToken);
}
