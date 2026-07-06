using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Shared.Identity;
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
            new EsiOptions { ClientId = "test" }, NullLogger<ClientTokenRefreshService>.Instance);

        var first = await service.EnsureValidAsync(charId, ct);
        var second = await service.EnsureValidAsync(charId, ct); // immediately again — must hit the back-off, not SSO

        Assert.Equal(TokenStatus.TemporarilyUnavailable, first);
        Assert.Equal(TokenStatus.TemporarilyUnavailable, second);
        Assert.Equal(1, auth.RefreshCalls); // backed off → the second call did not re-refresh against EVE SSO
    }

    [Fact]
    public async Task EnsureValid_WhenTokenStillValid_ReturnsValid_WithoutRefreshing()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeTokenStore(new EsiTokenSet("good", "refresh-token", DateTimeOffset.UtcNow + TimeSpan.FromHours(1)));
        var auth = new CountingAuthClient(new EsiTokenSet("x", "y", DateTimeOffset.UtcNow));
        var service = new ClientTokenRefreshService(new EmptyRegistry(), store, auth, new ThrowingJwtValidator(),
            new EsiOptions { ClientId = "test" }, NullLogger<ClientTokenRefreshService>.Instance);

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
            new EsiOptions { ClientId = "test" }, NullLogger<ClientTokenRefreshService>.Instance);

        var status = await service.EnsureValidAsync(100, ct);

        Assert.Equal(TokenStatus.NeedsReauth, status);
    }

    [Fact]
    public async Task EnsureValid_WhenNoRefreshToken_ReportsNeedsReauth_WithoutHittingSso()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeTokenStore(new EsiTokenSet("stale", "", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)));
        var auth = new CountingAuthClient(new EsiTokenSet("x", "y", DateTimeOffset.UtcNow));
        var service = new ClientTokenRefreshService(new EmptyRegistry(), store, auth, new ThrowingJwtValidator(),
            new EsiOptions { ClientId = "test" }, NullLogger<ClientTokenRefreshService>.Instance);

        var status = await service.EnsureValidAsync(100, ct);

        Assert.Equal(TokenStatus.NeedsReauth, status);
        Assert.Equal(0, auth.RefreshCalls); // nothing to refresh with → don't even call SSO
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
        private EsiTokenSet _tokens = tokens;
        public Task SaveAsync(int characterId, EsiTokenSet t, CancellationToken cancellationToken = default) { _tokens = t; return Task.CompletedTask; }
        public Task<EsiTokenSet?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<EsiTokenSet?>(_tokens);
        public Task<IReadOnlyList<int>> ListCharacterIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<int>>([]);
        public Task RemoveAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

    private sealed class ThrowingJwtValidator : IEsiJwtValidator
    {
        public Task<EsiIdentity> ValidateAsync(string accessToken, string clientId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("ESI access token failed validation.");
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
