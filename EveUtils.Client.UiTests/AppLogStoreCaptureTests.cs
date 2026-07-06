using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Logging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The in-app log store's capture policy: it keeps Warning and above so expected-but-noteworthy conditions —
/// an ESI 404 such as "character is not in a fleet" — stay visible in the log window without sitting in the error
/// category. Covers the threshold (Warning captured, Information dropped) and that a 404 through the real ESI pivot +
/// retry chain is captured as Warning, never Error.
/// </summary>
public class AppLogStoreCaptureTests
{
    [Fact]
    public void Store_CapturesWarningAndAbove_DropsInformation()
    {
        var store = new InMemoryLogStore();
        using var provider = BuildLogging(store);
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("EveUtils.Test");

        logger.LogInformation("informational — not an issue, must not be captured");
        logger.LogWarning("a warning worth a glance");
        logger.LogError("an actual error");

        var levels = store.GetAll().Select(e => e.Level).ToList();
        Assert.DoesNotContain(LogLevel.Information, levels);
        Assert.Contains(LogLevel.Warning, levels);
        Assert.Contains(LogLevel.Error, levels);
    }

    [Fact]
    public async Task Esi404_IsCapturedAsWarning_NeverError()
    {
        var store = new InMemoryLogStore();
        using var provider = BuildLogging(store);

        // Drive a 404 through the real retry handler + pivot, wired to the in-app store. The pivot logs the 404 once as
        // Warning (the retry layer no longer double-logs a non-retried 4xx); the store must capture it, never as Error.
        var retry = new EsiRetryHandler(
            new EsiRetryPolicy([], TimeSpan.Zero),
            new EsiOutageDetector(new EsiAvailabilityState()),
            provider.GetRequiredService<ILogger<EsiRetryHandler>>())
        {
            InnerHandler = new Stub404Handler()
        };
        var esi = new EsiClient(
            new SingleClientFactory(new HttpClient(retry)),
            new UnusedTokenProvider(),
            new EsiOutageDetector(new EsiAvailabilityState()),
            provider.GetRequiredService<ILogger<EsiClient>>());

        var result = await esi.RequestAsync<object>(
            EsiRequest.Get("/characters/77/fleet/"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(EsiErrorKind.NotFound, result.Error!.Kind);

        var entries = store.GetAll();
        Assert.NotEmpty(entries);                                          // still visible in the log window
        Assert.All(entries, e => Assert.Equal(LogLevel.Warning, e.Level)); // not an error — never red
    }

    [Fact]
    public async Task Esi404_WhenExpected_IsLoggedAtDebug_BelowTheCaptureThreshold()
    {
        var store = new InMemoryLogStore();
        using var provider = BuildLogging(store);

        // The same real pivot + retry chain, but the caller flags the 404 as expected (a 60s self-report poll for a
        // member who isn't in-game). The pivot then logs it at Debug, below the Warning capture threshold, so the log
        // window stays clean while the call still resolves to a handled NotFound.
        var retry = new EsiRetryHandler(
            new EsiRetryPolicy([], TimeSpan.Zero),
            new EsiOutageDetector(new EsiAvailabilityState()),
            provider.GetRequiredService<ILogger<EsiRetryHandler>>())
        {
            InnerHandler = new Stub404Handler()
        };
        var esi = new EsiClient(
            new SingleClientFactory(new HttpClient(retry)),
            new UnusedTokenProvider(),
            new EsiOutageDetector(new EsiAvailabilityState()),
            provider.GetRequiredService<ILogger<EsiClient>>());

        var result = await esi.RequestAsync<object>(
            EsiRequest.Get("/characters/77/fleet/", expectedNotFound: true), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(EsiErrorKind.NotFound, result.Error!.Kind); // still a handled NotFound — only the log level changed
        Assert.Empty(store.GetAll());                            // logged at Debug → not captured → no log-window noise
    }

    [Fact]
    public async Task EsiWithheldByGate_MapsToUnavailable_AndIsNotCapturedAsError()
    {
        var store = new InMemoryLogStore();
        using var provider = BuildLogging(store);

        // ESI is down: the gate withholds a non-status call before it reaches the network and tags its synthetic 503.
        // The pivot must recognise that tag, surface EsiErrorKind.Unavailable, and log it at Debug — so the withheld
        // call never sits in the log window as an error.
        var state = new EsiAvailabilityState();
        state.Set(EsiAvailability.Maintenance);
        var gate = new EsiGatingHandler(state, TimeProvider.System) { InnerHandler = new NeverHandler() };
        var esi = new EsiClient(
            new SingleClientFactory(new HttpClient(gate)),
            new UnusedTokenProvider(),
            new EsiOutageDetector(new EsiAvailabilityState()),
            provider.GetRequiredService<ILogger<EsiClient>>());

        var result = await esi.RequestAsync<object>(
            EsiRequest.Get("/characters/77/skills/"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(EsiErrorKind.Unavailable, result.Error!.Kind);
        Assert.Empty(store.GetAll()); // logged at Debug → below the Warning capture threshold → no error noise
    }

    private static ServiceProvider BuildLogging(ILogStore store) =>
        new ServiceCollection()
            .AddLogging(b =>
            {
                b.SetMinimumLevel(LogLevel.Trace); // let the AppLogger threshold be the sole gate under test
                b.AddProvider(new AppLoggerProvider(store));
            })
            .BuildServiceProvider();

    private sealed class Stub404Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":\"Character is not in a fleet\"}")
            });
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    // Proves the gate short-circuits before the network: if the call ever reaches here, the test fails loudly.
    private sealed class NeverHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A withheld call must never reach the network.");
    }

    private sealed class UnusedTokenProvider : IEsiTokenProvider
    {
        public Task<EsiAuthorization> AuthorizeAsync(int characterId, IReadOnlyList<string> requiredScopes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A public call must not reach the token provider.");
    }
}
