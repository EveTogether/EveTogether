using System;
using System.Collections.Generic;
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
/// What happens when ESI itself refuses a token this client still believed was good (ET-121).
///
/// <para>On the morning this ticket came from, two characters' every ESI call came back
/// <c>401 Unauthorized — Invalid token</c> while their badge stayed green and their location watches sat stopped for
/// four hours. The local expiry clock said the token was fine, so nothing refreshed it, and ESI's opinion had no way
/// back into the status the UI reads. Both halves of that are fixed here: the verdict reaches the badge, and the
/// next check refreshes instead of believing the clock.</para>
/// </summary>
public class EsiTokenRefusalTests
{
    private const int Character = 90250177;

    [Fact]
    public async Task ATokenEsiRefused_IsRefreshed_EvenThoughItsOwnClockSaysItIsStillGood()
    {
        var ct = TestContext.Current.CancellationToken;
        // An hour left on the clock: the refresh loop would not have touched this token for another 55 minutes.
        var store = new FakeTokenStore(new EsiTokenSet("refused", "refresh-1", DateTimeOffset.UtcNow + TimeSpan.FromHours(1)));
        var auth = new CountingAuthClient(new EsiTokenSet("fresh", "refresh-2", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20)));
        var service = Service(store, auth, out _);

        Assert.Equal(TokenStatus.Valid, await service.EnsureValidAsync(Character, ct)); // before: nothing to do

        await service.RecordRefusalAsync(Character, ct);
        var afterRefusal = await service.EnsureValidAsync(Character, ct);

        Assert.Equal(TokenStatus.Refreshed, afterRefusal);
        Assert.Equal(1, auth.RefreshCalls);
        Assert.Equal(0, store.RemoveCalls); // a refused access token is not a dead account
    }

    [Fact]
    public async Task ARefusalPutsTheCharacterOnTheBadge_Immediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeTokenStore(new EsiTokenSet("refused", "refresh-1", DateTimeOffset.UtcNow + TimeSpan.FromHours(1)));
        var service = Service(store, new CountingAuthClient(new EsiTokenSet("x", "y", DateTimeOffset.UtcNow)), out var tracker);

        await service.RecordRefusalAsync(Character, ct);

        // Not "signed out" and not "unavailable" — the badge has to be able to say which of the three it is, or the
        // pilot reads the wrong remedy off it.
        Assert.Equal(TokenStatus.Rejected, tracker.Get(Character));
    }

    [Fact]
    public async Task AStreamOfRefusals_DoesNotBecomeAStreamOfSsoRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeTokenStore(new EsiTokenSet("refused", "refresh-1", DateTimeOffset.UtcNow + TimeSpan.FromHours(1)));
        var auth = new CountingAuthClient(new EsiTokenSet("fresh", "refresh-2", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20)));
        var service = Service(store, auth, out _);

        await service.RecordRefusalAsync(Character, ct);
        Assert.Equal(TokenStatus.Refreshed, await service.EnsureValidAsync(Character, ct));

        // ESI is still refusing the freshly rotated token. Every poller in the app is on a 5 s loop, so honouring
        // each refusal would mean an SSO round-trip several times a second.
        await service.RecordRefusalAsync(Character, ct);
        var duringCooldown = await service.EnsureValidAsync(Character, ct);

        Assert.Equal(TokenStatus.Rejected, duringCooldown); // and it keeps saying so rather than going quietly green
        Assert.Equal(1, auth.RefreshCalls);
        Assert.Equal(0, store.RemoveCalls);
    }

    private static ClientTokenRefreshService Service(
        FakeTokenStore store, CountingAuthClient auth, out EsiTokenStatusTracker tracker)
    {
        tracker = new EsiTokenStatusTracker(new InProcessEventBus());
        return new ClientTokenRefreshService(new EmptyRegistry(), store, auth, new PassingJwtValidator(),
            new EsiOptions { ClientId = "test" }, tracker, NullLogger<ClientTokenRefreshService>.Instance);
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

    private sealed class PassingJwtValidator : IEsiJwtValidator
    {
        public Task<EsiIdentity> ValidateAsync(string accessToken, string clientId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EsiIdentity(Character, "Jithran", []));
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
