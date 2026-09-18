using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Location;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-308: every wake-up from sleep put "ESI sign-in expired" on screen for all six characters, and a minute later
/// they came back on their own. The network is a few seconds behind the resume, so the token refresh failed on DNS and
/// backed off; ESI then answered the stale token with 401 and the location watch turned that into a sign-in alarm.
/// <para>These tests keep the two cases apart that used to be one: a refresh that cannot reach EVE SSO (transient —
/// "reconnecting", no prompt, recovers by itself) and a refresh the SSO refuses (definitive — the prompt, at once).
/// They run the real chain: refresh service → token provider → ESI pivot → location client → location watch.</para>
/// </summary>
public class EsiWakeUpSignInTests
{
    private const int Character = 90250177;
    private const int Jita = 30000142;

    [Fact]
    public async Task AWakeUpWhereDnsFailsThenRecovers_ShowsReconnecting_NeverAsksToSignInAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        // The token aged past its lifetime while the machine slept; DNS is not back yet.
        var chain = new Chain(new EsiTokenSet("stale", "refresh-1", DateTimeOffset.UtcNow - TimeSpan.FromHours(8)));
        chain.Sso.Reachable = false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var readings = new ConcurrentQueue<EsiLocationReading>();
        var watching = chain.Monitor.WatchAsync(Character, "Jithran", reading =>
        {
            readings.Enqueue(reading);
            if (reading.SolarSystemId is not null) cts.Cancel();
        }, cts.Token);

        await chain.WaitForStatusAsync(TokenStatus.Reconnecting, ct);
        // The watch keeps polling through the outage; give it a good number of polls to prove it does not give up.
        await chain.WaitForLocationPollsAsync(30, ct);

        // The network comes back — what the resume/network-available hook does.
        chain.Sso.Reachable = true;
        chain.Refresh.RetryNow();
        await watching.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Empty(chain.Toasts.Toasts);                                  // no "ESI sign-in expired"
        Assert.DoesNotContain(TokenStatus.NeedsReauth, chain.Statuses);     // never "sign in again"
        Assert.Equal([TokenStatus.Reconnecting, TokenStatus.Refreshed], chain.Statuses.Distinct());
        Assert.Equal(0, chain.Store.RemoveCalls);                           // the sign-in was kept
        Assert.DoesNotContain(readings, reading => reading.SolarSystemId is null); // the watch was never lost
        Assert.Equal(Jita, readings.Last().SolarSystemId);
        // One refresh attempt for the outage, one after it: the polls in between waited instead of hammering SSO.
        Assert.Equal(2, chain.Sso.RefreshCalls);
    }

    [Fact]
    public async Task AStaleTokenEsiRefuses_WhileSsoIsUnreachable_IsPending_ThenRecoversOnRetryNow()
    {
        var ct = TestContext.Current.CancellationToken;
        // The in-flight case from the log: a token this client still believes in, sent after the wake, refused by ESI.
        var chain = new Chain(new EsiTokenSet("stale", "refresh-1", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30)));
        chain.Sso.Reachable = false;

        var duringOutage = await chain.Locations.GetLocationAsync(Character, ct);

        Assert.Equal(EsiErrorKind.AuthPending, duringOutage.Error?.Kind); // not AuthRequired: nothing was refused by SSO
        Assert.Equal(TokenStatus.Reconnecting, chain.Tracker.Get(Character));

        chain.Sso.Reachable = true;
        chain.Refresh.RetryNow();
        var afterwards = await chain.Locations.GetLocationAsync(Character, ct);

        // The refusal was still pending, so the renewal is forced right away rather than trusting the stale token's
        // clock for another minute of 401s.
        Assert.True(afterwards.IsSuccess, afterwards.Error?.Message);
        Assert.Equal(Jita, afterwards.Value!.SolarSystemId);
        Assert.Equal(0, chain.Store.RemoveCalls);
    }

    [Fact]
    public async Task ARevokedRefreshToken_AsksToSignInAgain_AtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var chain = new Chain(new EsiTokenSet("stale", "refresh-1", DateTimeOffset.UtcNow - TimeSpan.FromHours(8)));
        chain.Sso.Revoked = true;

        var readings = new List<EsiLocationReading>();
        await chain.Monitor.WatchAsync(Character, "Jithran", readings.Add, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        // One poll, one verdict: no retry window to sit out when the SSO has said no.
        Assert.Equal(1, chain.Sso.RefreshCalls);
        Assert.Equal(TokenStatus.NeedsReauth, chain.Tracker.Get(Character));
        var toast = Assert.Single(chain.Toasts.Toasts);
        Assert.Equal("ESI sign-in expired", toast.Title);
        Assert.Contains("Jithran", toast.Message);
        var lost = Assert.Single(readings);
        Assert.Null(lost.SolarSystemId);
        Assert.Equal(EsiErrorKind.AuthRequired, lost.Reason);
    }

    [Fact]
    public async Task RetryNow_EndsTheOutageAtOnce_InsteadOfAfterTheBackoff()
    {
        var ct = TestContext.Current.CancellationToken;
        var chain = new Chain(new EsiTokenSet("stale", "refresh-1", DateTimeOffset.UtcNow - TimeSpan.FromHours(8)));
        chain.Sso.Reachable = false;

        await chain.Refresh.StartAsync(ct);
        try
        {
            chain.Refresh.RetryNow(); // the resume
            await chain.WaitForStatusAsync(TokenStatus.Reconnecting, ct);

            chain.Sso.Reachable = true;
            var clock = Stopwatch.StartNew();
            chain.Refresh.RetryNow(); // the network comes back
            await chain.WaitForStatusAsync(TokenStatus.Refreshed, ct);
            clock.Stop();

            // Before ET-308 this waited out a 60 s back-off; now it is one SSO round-trip after the network returns.
            Assert.True(clock.Elapsed < ClientTokenRefreshService.UnreachableBackoff,
                $"recovered after {clock.Elapsed.TotalMilliseconds:0} ms");
        }
        finally
        {
            await chain.Refresh.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnUnreachableSso_IsLoggedAsUnreachable_NotAsATokenThatFailedValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        var chain = new Chain(new EsiTokenSet("stale", "refresh-1", DateTimeOffset.UtcNow - TimeSpan.FromHours(8)));
        chain.Sso.Reachable = false;

        await chain.Refresh.EnsureValidAsync(Character, ct);

        var warning = Assert.Single(chain.Log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("Could not reach EVE SSO", warning.Message);
        Assert.Contains("host unreachable", warning.Message);
        Assert.DoesNotContain("failed validation", warning.Message);
    }

    public static TheoryData<string, Exception, bool, bool> Failures => new()
    {
        // name, exception, unreachable, revoked
        { "DNS", new HttpRequestException("No such host is known. (login.eveonline.com:443)", new SocketException(11001)), true, false },
        { "timeout", new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"), true, false },
        { "JWKS fetch inside validation", new InvalidOperationException("ESI access token failed validation.", new HttpRequestException("down")), true, false },
        { "SSO 503", new EsiTokenExchangeException(503, "<html>Service Unavailable</html>"), true, false },
        { "empty 400 from a proxy", new EsiTokenExchangeException(400, ""), true, false },
        { "invalid_grant", new EsiTokenExchangeException(400, """{"error":"invalid_grant","error_description":"Invalid refresh token."}"""), false, true },
        { "401 with a body", new EsiTokenExchangeException(401, """{"error":"invalid_client"}"""), false, true },
        { "clock skew", new InvalidOperationException("ESI access token failed validation."), false, false },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void ARefreshFailure_IsClassifiedByWhetherTheSsoAnswered(string name, Exception failure, bool unreachable, bool revoked)
    {
        Assert.True(unreachable == ClientTokenRefreshService.IsUnreachable(failure), $"{name}: unreachable");
        Assert.True(revoked == ClientTokenRefreshService.IsRevoked(failure), $"{name}: revoked");
    }

    /// <summary>The real services from the refresh loop up to the location watch, over a fake SSO and a fake ESI.</summary>
    private sealed class Chain
    {
        public FakeTokenStore Store { get; }
        public FakeSso Sso { get; } = new();
        public EsiTokenStatusTracker Tracker { get; } = new(new InProcessEventBus());
        public ClientTokenRefreshService Refresh { get; }
        public IEsiLocationClient Locations { get; }
        public EsiLocationMonitor Monitor { get; }
        public RecordingToastService Toasts { get; } = new();
        public ListLogger<ClientTokenRefreshService> Log { get; } = new();
        public ConcurrentQueue<TokenStatus> Statuses { get; } = new();
        private readonly FakeEsi _esi = new();

        public Chain(EsiTokenSet tokens)
        {
            Store = new FakeTokenStore(tokens);
            var registry = new OneCharacterRegistry(new Character("Jithran", Character, [LocationScopeCatalog.ReadLocation]));
            Tracker.Changed += (_, status) => Statuses.Enqueue(status);
            Refresh = new ClientTokenRefreshService(registry, Store, Sso, new PassingJwtValidator(),
                new EsiOptions { ClientId = "test" }, Tracker, Log);
            var provider = new ClientEsiTokenProvider(registry, Store, Refresh);
            var esi = new EsiClient(new SingleClientFactory(new HttpClient(_esi)), provider,
                new RecordingEsiOutageDetector(), NullLogger<EsiClient>.Instance);
            Locations = new CountingLocations(new EsiLocationClient(esi), this);
            Monitor = new EsiLocationMonitor(Locations, Toasts, new ServiceCollection().BuildServiceProvider(),
                NullLogger<EsiLocationMonitor>.Instance)
            {
                PollInterval = TimeSpan.FromMilliseconds(1),
            };
            Monitor.UiReady();
        }

        public int LocationPolls;

        public Task WaitForStatusAsync(TokenStatus status, CancellationToken ct) =>
            Until(() => Tracker.Get(Character) == status, ct);

        public Task WaitForLocationPollsAsync(int polls, CancellationToken ct)
        {
            var target = Volatile.Read(ref LocationPolls) + polls;
            return Until(() => Volatile.Read(ref LocationPolls) >= target, ct);
        }

        private static async Task Until(Func<bool> condition, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
                await Task.Delay(1, ct);
            }
        }
    }

    private sealed class CountingLocations(IEsiLocationClient inner, Chain chain) : IEsiLocationClient
    {
        public Task<EsiResult<EsiCharacterLocation>> GetLocationAsync(int characterId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref chain.LocationPolls);
            return inner.GetLocationAsync(characterId, cancellationToken);
        }
    }

    /// <summary>EVE SSO: unreachable (DNS), revoking, or handing out a fresh token.</summary>
    private sealed class FakeSso : IEsiAuthClient
    {
        public volatile bool Reachable = true;
        public volatile bool Revoked;
        private int _refreshCalls;
        public int RefreshCalls => Volatile.Read(ref _refreshCalls);

        public Task<EsiTokenSet> RefreshAsync(string refreshToken, string clientId, string? clientSecret = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _refreshCalls);
            if (!Reachable)
                throw new HttpRequestException("No such host is known. (login.eveonline.com:443)",
                    new SocketException((int)SocketError.HostNotFound));
            if (Revoked)
                throw new EsiTokenExchangeException(400, """{"error":"invalid_grant","error_description":"Invalid refresh token. Token missing/expired."}""");
            return Task.FromResult(new EsiTokenSet("fresh", "refresh-2", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20)));
        }

        public Task<EsiTokenSet> ExchangePublicAsync(string code, Pkce pkce, string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangePkceConfidentialAsync(string code, Pkce pkce, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EsiTokenSet> ExchangeConfidentialAsync(string code, string clientId, string clientSecret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>ESI: answers the fresh token with a location and anything else the way it did at 08:00:20.</summary>
    private sealed class FakeEsi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Headers.Authorization?.Parameter == "fresh"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"solar_system_id":{{Jita}}}""") }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"Unauthorized - Invalid token"}""") });
    }

    private sealed class FakeTokenStore(EsiTokenSet tokens) : IPerCharacterTokenStore
    {
        private EsiTokenSet? _tokens = tokens;
        public int RemoveCalls { get; private set; }
        public Task SaveAsync(int characterId, EsiTokenSet t, CancellationToken cancellationToken = default) { _tokens = t; return Task.CompletedTask; }
        public Task<EsiTokenSet?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(_tokens);
        public Task<IReadOnlyList<int>> ListCharacterIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<int>>([Character]);
        public Task RemoveAsync(int characterId, CancellationToken cancellationToken = default) { RemoveCalls++; _tokens = null; return Task.CompletedTask; }
    }

    private sealed class PassingJwtValidator : IEsiJwtValidator
    {
        public Task<EsiIdentity> ValidateAsync(string accessToken, string clientId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EsiIdentity(Character, "Jithran", [LocationScopeCatalog.ReadLocation]));
    }

    private sealed class OneCharacterRegistry(Character character) : ICharacterRegistry
    {
        public event Action RegistryChanged { add { } remove { } }
        public Task AddOrUpdateAsync(Character c, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Character>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Character>>([character]);
        public Task RemoveAsync(int esiCharacterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReorderAsync(IReadOnlyList<int> orderedEsiCharacterIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    public sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
