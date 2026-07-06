using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi.Http;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ESI downtime gating: during EVE's daily ~11:00 UTC maintenance — or any failed /status/ poll —
/// the gating handler withholds non-essential calls while always letting /status/ through, and the availability
/// state drives both the gate and the client's downtime banner.
/// </summary>
public class EsiDowntimeGatingTests
{
    [Theory]
    [InlineData(11, 0, true)]
    [InlineData(11, 2, true)]
    [InlineData(11, 3, false)]
    [InlineData(10, 59, false)]
    [InlineData(12, 0, false)]
    public void IsScheduledWindow_CoversTheElevenUtcWindowOnly(int hour, int minute, bool expected)
    {
        var now = new DateTimeOffset(2026, 6, 5, hour, minute, 0, TimeSpan.Zero);
        Assert.Equal(expected, EsiDowntime.IsScheduledWindow(now));
    }

    [Fact]
    public void AvailabilityState_DefaultsToUsable_AndFlips()
    {
        var state = new EsiAvailabilityState();
        Assert.True(state.IsUsable);

        state.Set(EsiAvailability.Maintenance);
        Assert.False(state.IsUsable);
        Assert.Equal(EsiAvailability.Maintenance, state.Current);

        state.Set(EsiAvailability.Available);
        Assert.True(state.IsUsable);
    }

    [Fact]
    public async Task Gating_LetsCallsThrough_WhenAvailableOutsideTheWindow()
    {
        var (handler, inner) = Build(EsiAvailability.Available, OutsideWindow);
        using var invoker = new HttpMessageInvoker(handler);

        var response = await invoker.SendAsync(Get("https://esi.evetech.net/characters/123/fittings/"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Gating_WithholdsNonStatusCalls_DuringMaintenance()
    {
        var (handler, inner) = Build(EsiAvailability.Maintenance, OutsideWindow);
        using var invoker = new HttpMessageInvoker(handler);

        var response = await invoker.SendAsync(Get("https://esi.evetech.net/characters/123/fittings/"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, inner.Calls); // never reached the network
        Assert.True(response.Headers.Contains(EsiGateHeaders.Withheld)); // tagged so the pivot logs it quietly as Unavailable
    }

    [Fact]
    public async Task Gating_AlwaysLetsStatusThrough_EvenDuringMaintenance()
    {
        var (handler, inner) = Build(EsiAvailability.Maintenance, OutsideWindow);
        using var invoker = new HttpMessageInvoker(handler);

        var response = await invoker.SendAsync(Get("https://esi.evetech.net/status/"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.Calls); // the poller must keep detecting recovery
    }

    [Fact]
    public async Task Gating_WithholdsNonStatusCalls_DuringTheScheduledWindow_EvenIfNotYetObservedDown()
    {
        var (handler, inner) = Build(EsiAvailability.Available, new DateTimeOffset(2026, 6, 5, 11, 1, 0, TimeSpan.Zero));
        using var invoker = new HttpMessageInvoker(handler);

        var response = await invoker.SendAsync(Get("https://esi.evetech.net/characters/123/fittings/"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, inner.Calls);
    }

    private static readonly DateTimeOffset OutsideWindow = new(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);

    private static (EsiGatingHandler Handler, CountingInner Inner) Build(EsiAvailability availability, DateTimeOffset now)
    {
        var state = new EsiAvailabilityState();
        state.Set(availability);
        var inner = new CountingInner();
        var handler = new EsiGatingHandler(state, new FixedTime(now)) { InnerHandler = inner };
        return (handler, inner);
    }

    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CountingInner : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
