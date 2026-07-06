using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The failure-driven outage trip: a run of consecutive server-side failures while ESI is believed up raises
/// OutageSuspected (the cue to verify /status/ early), any real ESI response resets the run, and once the gate is shut
/// the detector stays quiet so withheld calls can't keep it tripping. Also covers that the pivot feeds it the right
/// outcome per error kind.
/// </summary>
public class EsiOutageDetectorTests
{
    private const int Threshold = 10;

    [Fact]
    public void Trips_AfterTenConsecutiveServerFailures()
    {
        var detector = new EsiOutageDetector(new EsiAvailabilityState());
        var trips = CountTrips(detector);

        for (var i = 0; i < Threshold - 1; i++)
            detector.RecordServerFailure();
        Assert.Equal(0, trips()); // nine in a row is not enough

        detector.RecordServerFailure();
        Assert.Equal(1, trips()); // the tenth trips
    }

    [Fact]
    public void Success_ResetsTheRun()
    {
        var detector = new EsiOutageDetector(new EsiAvailabilityState());
        var trips = CountTrips(detector);

        for (var i = 0; i < Threshold - 1; i++)
            detector.RecordServerFailure();
        detector.RecordSuccess(); // a single good (or reachable-4xx) call clears the run
        for (var i = 0; i < Threshold - 1; i++)
            detector.RecordServerFailure();

        Assert.Equal(0, trips()); // never reached ten in a row
    }

    [Fact]
    public void DoesNotCount_WhileEsiIsAlreadyDown()
    {
        var availability = new EsiAvailabilityState();
        availability.Set(EsiAvailability.Maintenance);
        var detector = new EsiOutageDetector(availability);
        var trips = CountTrips(detector);

        for (var i = 0; i < Threshold * 2; i++)
            detector.RecordServerFailure(); // withheld/gated calls must not feed the count

        Assert.Equal(0, trips());
    }

    [Fact]
    public void ReArms_AfterTripping()
    {
        var detector = new EsiOutageDetector(new EsiAvailabilityState());
        var trips = CountTrips(detector);

        for (var i = 0; i < Threshold * 2; i++)
            detector.RecordServerFailure();

        Assert.Equal(2, trips()); // each fresh run of ten trips once — not a continuous storm
    }

    [Fact]
    public void Reset_ClearsAPartialRun()
    {
        var detector = new EsiOutageDetector(new EsiAvailabilityState());
        var trips = CountTrips(detector);

        for (var i = 0; i < Threshold - 1; i++)
            detector.RecordServerFailure();
        detector.Reset(); // recovery clears the stale count
        detector.RecordServerFailure();

        Assert.Equal(0, trips());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1, 0)] // 503 → server failure
    [InlineData(HttpStatusCode.GatewayTimeout, 1, 0)]     // 504 → Timeout → server failure
    [InlineData(HttpStatusCode.NotFound, 0, 1)]           // 404 → ESI answered → reset (success)
    [InlineData(HttpStatusCode.Forbidden, 0, 1)]          // 403 → ESI answered → reset (success)
    public async Task Pivot_FeedsTheDetector_PerOutcome(HttpStatusCode status, int expectedFailures, int expectedSuccesses)
    {
        var recorder = new RecordingEsiOutageDetector();
        var esi = new EsiClient(
            new SingleClientFactory(new HttpClient(new FixedStatusHandler(status))),
            new UnusedTokenProvider(),
            recorder,
            NullLogger<EsiClient>.Instance);

        await esi.RequestAsync<object>(EsiRequest.Get("/characters/1/skills/"), CancellationToken.None);

        Assert.Equal(expectedFailures, recorder.ServerFailures);
        Assert.Equal(expectedSuccesses, recorder.Successes);
    }

    private static Func<int> CountTrips(IEsiOutageDetector detector)
    {
        var count = 0;
        detector.OutageSuspected += () => count++;
        return () => count;
    }

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"x\"}") });
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
