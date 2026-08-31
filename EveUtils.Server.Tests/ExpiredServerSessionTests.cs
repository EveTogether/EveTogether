using EveUtils.Server.Auth;
using EveUtils.Shared.Modules.ServerAuth.Repositories.Implementations;
using EveUtils.Shared.Modules.ServerAuth.Services;
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
        var sessions = new ServerSessionService(repository);

        var issued = await sessions.IssueAsync(character.Id, ct);
        await ExpireAccessWindowAsync(repository, issued, ct);

        // What the server tells every unary RPC once the hour is up.
        Assert.Null(await sessions.ValidateAsync(issued.AccessToken, ct));

        // …and what the client can do about it without troubling the user.
        var refreshed = await sessions.RefreshAsync(issued.RefreshToken, ct);
        Assert.NotNull(refreshed);
        Assert.NotNull(await sessions.ValidateAsync(refreshed!.AccessToken, ct));
    }

    [Fact]
    public async Task AnExpiredRefreshToken_IsRefusedOutright_SoTheClientMustRePair()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new ServerAuthRepository(_factory);
        var character = await repository.UpsertSyncedAsync(90250177, "Jithran", new EncryptedToken([1], [2], [3]), null, ct);
        var sessions = new ServerSessionService(repository);

        var issued = await sessions.IssueAsync(character.Id, ct);
        await ExpireAccessWindowAsync(repository, issued, ct, refreshExpiresIn: TimeSpan.FromHours(-1));

        // The refusal (not a transport error) is what turns the character's server chip red instead of amber.
        Assert.Null(await sessions.RefreshAsync(issued.RefreshToken, ct));
    }

    /// <summary>Backdates a live session's access window without touching its tokens — exactly what an hour of
    /// uptime does to a client whose bus stream never dropped.</summary>
    private static async Task ExpireAccessWindowAsync(
        ServerAuthRepository repository, IssuedSession issued, CancellationToken cancellationToken,
        TimeSpan? refreshExpiresIn = null)
    {
        var now = DateTimeOffset.UtcNow;
        var session = await repository.FindSessionByAccessHashAsync(TokenSecurity.Hash(issued.AccessToken), cancellationToken);
        Assert.NotNull(session);
        await repository.RotateSessionAsync(
            session!.Id,
            TokenSecurity.Hash(issued.AccessToken),
            TokenSecurity.Hash(issued.RefreshToken),
            now.AddHours(-2), now.AddHours(-1), now + (refreshExpiresIn ?? TimeSpan.FromDays(300)),
            cancellationToken);
    }
}
