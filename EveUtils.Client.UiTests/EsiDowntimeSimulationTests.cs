using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Logging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Esi.Status;
using EveUtils.Shared.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// End-to-end downtime simulation: "play ESI" against the real shared ESI handler chain (header → cache → gating →
/// rate-limit → retry, production order) plus the real pivot, outage detector and /status poller that BOTH the client
/// and server host. A controllable fake ESI is flipped up/down and the whole lifecycle is driven and asserted:
/// detection on a failure burst, the gate withholding every non-status call before it leaves the machine, the log
/// staying quiet while gated, and a clean recovery with the counter reset (no instant re-trip). Caching is disabled on
/// the fake so each flip is observed at once (in production the ~30s /status cache adds the detection lag).
/// </summary>
public class EsiDowntimeSimulationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FullOutageLifecycle_AppGatesAndRecoversCleanly()
    {
        var store = new InMemoryLogStore();
        var esiServer = new FakeEsiServer();
        var (esi, availability, detector, statusPoller) = BuildRealPipeline(store, esiServer);

        var outageSuspected = 0;
        detector.OutageSuspected += () => outageSuspected++;

        // Every data call uses a fresh character id so the file cache never serves a hit — each call genuinely
        // exercises the gate/network path (the cache is covered by its own tests; here we want the live behaviour).
        var nextChar = 100;

        // ── PHASE 1 — ESI up ──────────────────────────────────────────────────────────────────────
        await statusPoller.PollOnceAsync(TestContext.Current.CancellationToken);
        var up = await esi.RequestAsync<object>(EsiRequest.Get($"/characters/{nextChar++}/skills/"), TestContext.Current.CancellationToken);
        Assert.True(availability.IsUsable);
        Assert.True(up.IsSuccess);
        Trace($"UP        | status=Available  data-call OK (reached ESI, DataHits={esiServer.DataHits})");

        // ── PHASE 2 — ESI goes down, detected by the failure burst ────────────────────────────────
        esiServer.IsDown = true;
        for (var i = 0; i < 10; i++)
        {
            var failed = await esi.RequestAsync<object>(EsiRequest.Get($"/characters/{nextChar++}/skills/"), TestContext.Current.CancellationToken);
            Assert.False(failed.IsSuccess);
            Assert.Equal(EsiErrorKind.ServerError, failed.Error!.Kind);
        }
        Assert.Equal(1, outageSuspected); // ten consecutive server failures tripped the detector exactly once
        Trace($"DETECT    | 10 data calls failed (ESI 503) → OutageSuspected fired ({outageSuspected}×) → poke /status");

        await statusPoller.PollOnceAsync(TestContext.Current.CancellationToken); // the poke makes the poller verify /status straight away
        Assert.False(availability.IsUsable);
        Trace("GATE SHUT | /status poll also 503 → availability=Maintenance, gate closed");

        // ── PHASE 3 — gated: not one more call leaves the machine ──────────────────────────────────
        var hitsBeforeGate = esiServer.DataHits;
        var errorsBeforeGate = ErrorCount(store);
        for (var i = 0; i < 5; i++)
        {
            var withheld = await esi.RequestAsync<object>(EsiRequest.Get($"/characters/{nextChar++}/skills/"), TestContext.Current.CancellationToken);
            Assert.False(withheld.IsSuccess);
            Assert.Equal(EsiErrorKind.Unavailable, withheld.Error!.Kind); // withheld locally, not a server round-trip
        }
        Assert.Equal(hitsBeforeGate, esiServer.DataHits); // ZERO of the 5 reached ESI
        Assert.Equal(errorsBeforeGate, ErrorCount(store)); // and they added no error-log noise
        Trace($"WITHHELD  | 5 gated calls → Unavailable, ESI untouched (DataHits still {esiServer.DataHits}), no new error logs");

        // ── PHASE 4 — recovery ────────────────────────────────────────────────────────────────────
        esiServer.IsDown = false;
        await statusPoller.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.True(availability.IsUsable);
        Trace("RECOVER   | /status poll OK → availability=Available, gate reopened, failure counter reset");

        var hitsBeforeResume = esiServer.DataHits;
        var resumed = await esi.RequestAsync<object>(EsiRequest.Get($"/characters/{nextChar++}/skills/"), TestContext.Current.CancellationToken);
        Assert.True(resumed.IsSuccess);
        Assert.Equal(hitsBeforeResume + 1, esiServer.DataHits); // calls flow again, and the reset means no instant re-trip
        Assert.Equal(1, outageSuspected); // still only the single trip from phase 2 — recovery didn't loop
        Trace($"RESUMED   | data call reached ESI and succeeded (DataHits={esiServer.DataHits}); no re-trip after recovery");

        foreach (var line in _trace)
            output.WriteLine(line);
    }

    private readonly List<string> _trace = [];
    private void Trace(string line) => _trace.Add(line);

    private static int ErrorCount(ILogStore store) => store.GetAll().Count(e => e.Level == LogLevel.Error);

    private static (IEsiClient Esi, IEsiAvailabilityState Availability, IEsiOutageDetector Detector, EveServerStatusService Poller)
        BuildRealPipeline(ILogStore store, FakeEsiServer esiServer)
    {
        var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(new AppLoggerProvider(store));
        });

        var availability = new EsiAvailabilityState();
        var detector = new EsiOutageDetector(availability);
        var monitor = new EsiRateLimitMonitor(NullLogger<EsiRateLimitMonitor>.Instance);
        var policy = new EsiRetryPolicy([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero], TimeSpan.Zero); // instant retries for the sim

        // Real handler chain, production order (outer → inner): header → gating → rate-limit → retry → fake ESI. The
        // cache handler is deliberately left out of THIS sim (it has its own tests): with the cache in, a fresh hit is
        // served locally during downtime — correct, but it would mask whether a call that *would* hit the network is
        // withheld, which is exactly what we want to observe here.
        var retry = new EsiRetryHandler(policy, detector, loggerFactory.CreateLogger<EsiRetryHandler>()) { InnerHandler = esiServer };
        var rateLimit = new EsiRateLimitHandler(monitor, NullLogger<EsiRateLimitHandler>.Instance) { InnerHandler = retry };
        var gating = new EsiGatingHandler(availability, TimeProvider.System) { InnerHandler = rateLimit };
        var header = new EsiHeaderHandler(new RuntimeContext(ExecutionHost.Client)) { InnerHandler = gating };
        var http = new HttpClient(header);

        var esi = new EsiClient(new SingleClientFactory(http), new UnusedTokenProvider(), detector, loggerFactory.CreateLogger<EsiClient>());
        var poller = new EveServerStatusService(esi, availability, detector, loggerFactory.CreateLogger<EveServerStatusService>());
        return (esi, availability, detector, poller);
    }

    /// <summary>The ESI server I control: every endpoint answers 200 when up and 503 when down; nothing is cacheable so
    /// each flip is seen at once. Counts how many calls actually reach it, to prove the gate withholds the rest.</summary>
    private sealed class FakeEsiServer : HttpMessageHandler
    {
        public bool IsDown { get; set; }
        public int DataHits { get; private set; }
        public int StatusHits { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (EsiStatusEndpoint.IsStatusPoll(request.RequestUri)) StatusHits++; else DataHits++;

            var isStatus = EsiStatusEndpoint.IsStatusPoll(request.RequestUri);
            var body = IsDown ? "{\"error\":\"EVE/ESI is in downtime\"}" : isStatus ? "{\"players\":1000}" : "{}";
            var response = new HttpResponseMessage(IsDown ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
            return Task.FromResult(response);
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class UnusedTokenProvider : IEsiTokenProvider
    {
        public Task<EsiAuthorization> AuthorizeAsync(int characterId, IReadOnlyList<string> requiredScopes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A public call must not reach the token provider.");
    }
}
