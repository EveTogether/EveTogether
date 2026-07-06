using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// "Play ESI" at the cache layer (cache → gating → fake): a stale entry with an ETag revalidates with a 304 and reuses
/// the stored body (no re-download), and — the downtime interaction — a still-fresh entry is served locally even while
/// the gate is shut, whereas a stale one that would need the network is withheld. Covers the claim in the gating
/// handler's own doc comment end-to-end.
/// </summary>
public class EsiCacheSimulationTests
{
    private const string Url = "https://esi.evetech.net/characters/1/skills/";

    [Fact]
    public async Task StaleEntryWithETag_Revalidates_AndReusesStoredBodyOn304()
    {
        var fake = new FakeEsi(HttpStatusCode.NotModified);
        var (invoker, _) = Wire(
            EsiAvailability.Available,
            new EsiCacheEntry("STORED ROWS", "\"etag-abc\"", Past(), Past()), // stored ETags keep their quotes (as the handler stores them)
            fake);

        var response = await invoker.SendAsync(Get(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(fake.SawIfNoneMatch);                          // the handler revalidated conditionally
        Assert.Equal("STORED ROWS", body);                         // 304 → the stored body was reused, not re-downloaded
        Assert.True(response.Headers.Contains(EsiCacheHeaders.FromCache));
    }

    [Fact]
    public async Task FreshEntry_IsServedFromCache_EvenWhileGated()
    {
        var fake = new FakeEsi(HttpStatusCode.ServiceUnavailable);
        var (invoker, _) = Wire(
            EsiAvailability.Maintenance, // ESI is down
            new EsiCacheEntry("FRESH ROWS", null, Future(), Now()),
            fake);

        var response = await invoker.SendAsync(Get(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("FRESH ROWS", body);
        Assert.Equal(0, fake.Calls); // a fresh hit short-circuits before the gate — served without touching ESI
    }

    [Fact]
    public async Task StaleEntry_IsWithheld_WhileGated()
    {
        var fake = new FakeEsi(HttpStatusCode.OK);
        var (invoker, _) = Wire(
            EsiAvailability.Maintenance,
            new EsiCacheEntry("STALE ROWS", null, Past(), Past()), // no ETag → needs a real network fetch
            fake);

        var response = await invoker.SendAsync(Get(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains(EsiGateHeaders.Withheld));
        Assert.Equal(0, fake.Calls); // a stale entry can't be revalidated while down — withheld, not served stale
    }

    private static (HttpMessageInvoker Invoker, FileEsiCacheStore Store) Wire(EsiAvailability availability, EsiCacheEntry seed, FakeEsi fake)
    {
        var store = new FileEsiCacheStore(Path.Combine(Path.GetTempPath(), "esi-cache-sim-" + Guid.NewGuid().ToString("N")));
        store.SetAsync(FileEsiCacheStore.KeyFor(Url), seed).GetAwaiter().GetResult();

        var state = new EsiAvailabilityState();
        state.Set(availability);
        var monitor = new EsiRateLimitMonitor(NullLogger<EsiRateLimitMonitor>.Instance);

        var gating = new EsiGatingHandler(state, TimeProvider.System) { InnerHandler = fake };
        var cache = new EsiCacheHandler(store, monitor) { InnerHandler = gating };
        return (new HttpMessageInvoker(cache), store);
    }

    private static HttpRequestMessage Get() => new(HttpMethod.Get, Url);
    private static DateTimeOffset Now() => DateTimeOffset.UtcNow;
    private static DateTimeOffset Past() => DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
    private static DateTimeOffset Future() => DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);

    private sealed class FakeEsi(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool SawIfNoneMatch { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Headers.IfNoneMatch.Count > 0)
                SawIfNoneMatch = true;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("NETWORK BODY") });
        }
    }
}
