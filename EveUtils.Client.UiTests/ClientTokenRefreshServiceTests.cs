using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// token refresh. When a refresh produces a token that still fails validation — almost always a local clock
/// skew vs EVE's token lifetime — the service must not throw: it reports <see cref="TokenStatus.TemporarilyUnavailable"/>
/// so background ESI pollers skip quietly instead of logging an error on every tick, and it backs off so it doesn't
/// re-hit EVE SSO on every 5s call during the window.
/// </summary>
public class ClientTokenRefreshServiceTests
{
    // Every outcome is recorded on a tracker; these tests are about the outcome itself, so a fresh throwaway
    // tracker on a bare in-process bus is enough. EsiTokenStatusTrackerTests covers what it does with them.
    private static EsiTokenStatusTracker Tracker() => new(new InProcessEventBus());

    [Fact]
    public async Task EnsureValid_WhenRefreshedTokenFailsValidation_ReportsTemporarilyUnavailable_AndBacksOff()
    {
        var ct = TestContext.Current.CancellationToken;
        const int charId = 100;

        // An expiring stored token forces the refresh path; the refresh "succeeds" but the validator rejects the
        // result (the clock-skew case), which used to throw and get logged as an error on every poll.
        var store = new FakeTokenStore(new EsiTokenSet("stale", "refresh-token", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)));
        var auth = new CountingAuthClient(new EsiTokenSet("refreshed", "refresh-token", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20)));
        var service = new ClientTokenRefreshService(new EmptyRegistry(), store, auth, new ThrowingJwtValidator(),
            new EsiOptions { ClientId = "test" }, Tracker(), NullLogger<ClientTokenRefreshService>.Instance);

        var first = await service.EnsureValidAsync(charId, ct);
        var second = await service.EnsureValidAsync(charId, ct); // immediately again — must hit the back-off, not SSO

        Assert.Equal(TokenStatus.TemporarilyUnavailable, first);
        Assert.Equal(TokenStatus.TemporarilyUnavailable, second);
        Assert.Equal(1, auth.RefreshCalls); // backed off → the second call did not re-refresh against EVE SSO
        Assert.Equal(0, store.RemoveCalls); // a clock-skew hiccup is recoverable — never delete the stored token for it
    }

    [Fact]
    public async Task EnsureValid_WhenTokenStillValid_ReturnsValid_WithoutRefreshing()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeTokenStore(new EsiTokenSet("good", "refresh-token", DateTimeOffset.UtcNow + TimeSpan.FromHours(1)));
        var auth = new CountingAuthClient(new EsiTokenSet("x", "y", DateTimeOffset.UtcNow));
        var service = new ClientTokenRefreshService(new EmptyRegistry(), store, auth, new ThrowingJwtValidator(),
            new EsiOptions { ClientId = "test" }, Tracker(), NullLogger<ClientTokenRefreshService>.Instance);

        var status = await service.EnsureValidAsync(100, ct);

        Assert.Equal(TokenStatus.Valid, status);
        Assert.Equal(0, auth.RefreshCalls); // a still-valid token needs no refresh
    }

    [Fact]
    public async Task EnsureValid_WhenRefreshIsRevoked_ReportsNeedsReauth()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeTokenStore(new EsiTokenSet("stale", "refresh-token", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)));
        var auth = new RevokingAuthClient(); // EVE SSO rejects the refresh token (invalid_grant) — re-auth is the only fix
        var service = new ClientTokenRefreshService(new EmptyRegistry(), store, auth, new ThrowingJwtValidator(),
            new EsiOptions { ClientId = "test" }, Tracker(), NullLogger<ClientTokenRefreshService>.Instance);

        var status = await service.EnsureValidAsync(100, ct);

        Assert.Equal(TokenStatus.NeedsReauth, status);
        Assert.Equal(1, store.RemoveCalls); // invalid_grant is permanent — the dead blob must not linger on disk (ET-54)
    }

    [Fact]
    public async Task EnsureValid_WhenNoRefreshToken_ReportsNeedsReauth_WithoutHittingSso()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeTokenStore(new EsiTokenSet("stale", "", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)));
        var auth = new CountingAuthClient(new EsiTokenSet("x", "y", DateTimeOffset.UtcNow));
        var service = new ClientTokenRefreshService(new EmptyRegistry(), store, auth, new ThrowingJwtValidator(),
            new EsiOptions { ClientId = "test" }, Tracker(), NullLogger<ClientTokenRefreshService>.Instance);

        var status = await service.EnsureValidAsync(100, ct);

        Assert.Equal(TokenStatus.NeedsReauth, status);
        Assert.Equal(0, auth.RefreshCalls); // nothing to refresh with → don't even call SSO
        Assert.Equal(1, store.RemoveCalls); // a token set without a refresh token can never recover — remove it (ET-54)
    }

    [Fact]
    public async Task EnsureValid_WhenValidationFailsAfterTheSsoRotatedTheRefreshToken_KeepsTheNewRefreshToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeTokenStore(new EsiTokenSet("stale", "refresh-1", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)));
        var auth = new CountingAuthClient(new EsiTokenSet("fresh", "refresh-2", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20)));
        var jwksDown = new ThrowingJwtValidator(new HttpRequestException("No such host is known. (login.eveonline.com:443)"));
        var service = new ClientTokenRefreshService(new EmptyRegistry(), store, auth, jwksDown,
            new EsiOptions { ClientId = "test" }, Tracker(), NullLogger<ClientTokenRefreshService>.Instance);

        var status = await service.EnsureValidAsync(100, ct);

        Assert.Equal(TokenStatus.Reconnecting, status);
        var stored = await store.LoadAsync(100, ct);
        Assert.Equal("refresh-2", stored?.RefreshToken); // refresh-1 is spent at EVE; offering it again is invalid_grant
        Assert.True(stored?.ExpiresAt < DateTimeOffset.UtcNow); // the unvalidated access token is not trusted
        Assert.Equal(0, store.RemoveCalls);
    }

    [Fact]
    public async Task EnsureValid_WhenTheSsoAnswersWithoutARefreshToken_KeepsTheCurrentOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeTokenStore(new EsiTokenSet("stale", "refresh-1", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)));
        var auth = new CountingAuthClient(new EsiTokenSet("fresh", null, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20)));
        var service = new ClientTokenRefreshService(new EmptyRegistry(), store, auth, new PassingJwtValidator(),
            new EsiOptions { ClientId = "test" }, Tracker(), NullLogger<ClientTokenRefreshService>.Instance);

        var status = await service.EnsureValidAsync(100, ct);

        Assert.Equal(TokenStatus.Refreshed, status);
        Assert.Equal("refresh-1", (await store.LoadAsync(100, ct))?.RefreshToken); // a null here signs the pilot out
    }

    [Theory]
    [InlineData(1, 9, 11)]
    [InlineData(2, 18, 22)]
    [InlineData(3, 36, 44)]
    [InlineData(40, 270, 330)]
    public void UnreachableDelay_DoublesPerAttempt_UpToTheCap_WithJitter(int attempt, double minSeconds, double maxSeconds)
    {
        var delay = ClientTokenRefreshService.UnreachableDelay(attempt).TotalSeconds;

        Assert.InRange(delay, minSeconds, maxSeconds);
    }

    [Fact]
    public async Task EnsureValid_WhenTheSsoAnswers503_BacksOffWithoutSigningOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeTokenStore(new EsiTokenSet("stale", "refresh-1", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)));
        var auth = new FailingAuthClient(new EsiTokenExchangeException(503, "<html>Service Unavailable</html>"));
        var service = new ClientTokenRefreshService(new EmptyRegistry(), store, auth, new PassingJwtValidator(),
            new EsiOptions { ClientId = "test" }, Tracker(), NullLogger<ClientTokenRefreshService>.Instance);

        Assert.Equal(TokenStatus.Reconnecting, await service.EnsureValidAsync(100, ct));
        Assert.Equal(TokenStatus.Reconnecting, await service.EnsureValidAsync(100, ct)); // inside the back-off

        Assert.Equal(1, auth.RefreshCalls);
        Assert.Equal(0, store.RemoveCalls); // a 503 is no verdict on the sign-in
    }

    private sealed class RevokingAuthClient : IEsiAuthClient
    {
        public Task<EsiTokenSet> RefreshAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("EVE SSO returned invalid_grant for the refresh token.");

        public Task<EsiTokenSet> ExchangePublicAsync(string code, Pkce pkce, string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangePkceConfidentialAsync(string code, Pkce pkce, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangeConfidentialAsync(string code, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeTokenStore(EsiTokenSet tokens) : IPerCharacterTokenStore
    {
        private EsiTokenSet? _tokens = tokens;
        public int RemoveCalls { get; private set; }
        public Task SaveAsync(int characterId, EsiTokenSet t, CancellationToken cancellationToken = default) { _tokens = t; return Task.CompletedTask; }
        public Task<EsiTokenSet?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(_tokens);
        public Task<IReadOnlyList<int>> ListCharacterIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<int>>([]);
        public Task RemoveAsync(int characterId, CancellationToken cancellationToken = default) { RemoveCalls++; _tokens = null; return Task.CompletedTask; }
    }

    private sealed class CountingAuthClient(EsiTokenSet refreshed) : IEsiAuthClient
    {
        public int RefreshCalls { get; private set; }

        public Task<EsiTokenSet> RefreshAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(refreshed);
        }

        public Task<EsiTokenSet> ExchangePublicAsync(string code, Pkce pkce, string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangePkceConfidentialAsync(string code, Pkce pkce, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangeConfidentialAsync(string code, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingJwtValidator(Exception? inner = null) : IEsiJwtValidator
    {
        public Task<EsiIdentity> ValidateAsync(string accessToken, string clientId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("ESI access token failed validation.", inner);
    }

    private sealed class PassingJwtValidator : IEsiJwtValidator
    {
        public Task<EsiIdentity> ValidateAsync(string accessToken, string clientId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EsiIdentity(100, "Jithran", []));
    }

    private sealed class FailingAuthClient(Exception failure) : IEsiAuthClient
    {
        public int RefreshCalls { get; private set; }

        public Task<EsiTokenSet> RefreshAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromException<EsiTokenSet>(failure);
        }

        public Task<EsiTokenSet> ExchangePublicAsync(string code, Pkce pkce, string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangePkceConfidentialAsync(string code, Pkce pkce, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangeConfidentialAsync(string code, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyRegistry : ICharacterRegistry
    {
        public event Action RegistryChanged { add { } remove { } }
        public Task AddOrUpdateAsync(Character character, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Character>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Character>>([]);
        public Task RemoveAsync(int esiCharacterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReorderAsync(IReadOnlyList<int> orderedEsiCharacterIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
