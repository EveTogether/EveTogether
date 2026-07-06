using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The ESI retry handler's downtime behaviour: a failed <c>/status/</c> poll is itself the "ESI is down" signal, so it
/// is never retried (no per-second burst against a dead API); other 5xx ride the bounded retry schedule. Delays are
/// zeroed here so the assertions are on attempt counts, not wall-clock.
/// </summary>
public class EsiRetryHandlerTests
{
    [Fact]
    public async Task StatusPoll_IsNotRetried_OnServerError()
    {
        var (handler, inner) = Build();
        using var invoker = new HttpMessageInvoker(handler);

        var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://esi.evetech.net/status/"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.Calls); // the status poll's own interval is the cadence — no retry burst
    }

    [Fact]
    public async Task NonStatusCall_IsRetried_AcrossTheWholeSchedule()
    {
        var (handler, inner) = Build();
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://esi.evetech.net/characters/1/skills/"),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, inner.Calls); // first try + 3 retries (the schedule length)
    }

    [Fact]
    public async Task NonStatusCall_IsNotRetried_WhenOutageAlreadySuspected()
    {
        var (handler, inner) = Build(new RecordingEsiOutageDetector { IsSuspect = true });
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://esi.evetech.net/characters/1/skills/"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, inner.Calls); // a suspected outage stops the retry pile-on — one attempt, then surface
    }

    [Fact]
    public async Task RateLimited429_WaitsRetryAfter_ThenRetries()
    {
        var inner = new ScriptedInner(
            RateLimited(HttpStatusCode.TooManyRequests, retryAfterSeconds: 1),
            Ok());
        var handler = new EsiRetryHandler(
            new EsiRetryPolicy([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero], TimeSpan.Zero),
            new EsiOutageDetector(new EsiAvailabilityState()),
            NullLogger<EsiRetryHandler>.Instance) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://esi.evetech.net/characters/1/skills/"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls); // backed off the 429, then the retry succeeded
    }

    [Fact]
    public async Task ErrorLimited420_Stops_WithoutRetrying()
    {
        var inner = new ScriptedInner(RateLimited((HttpStatusCode)420, retryAfterSeconds: 0));
        var handler = new EsiRetryHandler(
            new EsiRetryPolicy([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero], TimeSpan.Zero),
            new EsiOutageDetector(new EsiAvailabilityState()),
            NullLogger<EsiRetryHandler>.Instance) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://esi.evetech.net/characters/1/skills/"),
            TestContext.Current.CancellationToken);

        Assert.Equal(420, (int)response.StatusCode);
        Assert.Equal(1, inner.Calls); // 420 = error limit reached → stop at once, the gate handles the rest
    }

    [Fact]
    public void DefaultPolicy_RetriesOnOneThreeFiveSeconds()
    {
        var policy = EsiRetryPolicy.Default;

        Assert.Equal(3, policy.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.DelayFor(0));
        Assert.Equal(TimeSpan.FromSeconds(3), policy.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayFor(2));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayFor(9)); // the last entry holds for any further attempt
    }

    private static (EsiRetryHandler Handler, CountingInner Inner) Build(IEsiOutageDetector? outageDetector = null)
    {
        var policy = new EsiRetryPolicy([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero], TimeSpan.Zero);
        var inner = new CountingInner();
        var detector = outageDetector ?? new EsiOutageDetector(new EsiAvailabilityState());
        var handler = new EsiRetryHandler(policy, detector, NullLogger<EsiRetryHandler>.Instance) { InnerHandler = inner };
        return (handler, inner);
    }

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent("{}") };

    private static HttpResponseMessage RateLimited(HttpStatusCode status, int retryAfterSeconds)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"rate limited\"}") };
        if (retryAfterSeconds > 0)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
        return response;
    }

    private sealed class CountingInner : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":\"down\"}")
            });
        }
    }

    // Hands back the given responses in order, holding the last once exhausted.
    private sealed class ScriptedInner(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responses[Math.Min(Calls, responses.Length - 1)];
            Calls++;
            return Task.FromResult(response);
        }
    }
}
