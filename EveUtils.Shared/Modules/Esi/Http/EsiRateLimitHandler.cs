using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Pre-emptive rate-limit gate for both ESI systems, sitting just outside the retry handler.
/// Before a call it short-circuits a bucket that is hard-stopped on the 420 error limit (no socket call),
/// and throttles when a bucket runs low (CCP best-practice §4). After a call it parses both header sets
/// and feeds <see cref="IEsiRateLimitMonitor"/> — the per-bucket data layer for the metrics window.
/// </summary>
public sealed class EsiRateLimitHandler(
    IEsiRateLimitMonitor monitor,
    ILogger<EsiRateLimitHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bucketKey = EsiRequestOptions.ResolveBucketKey(request);
        var endpointKey = EsiRequestOptions.ResolveEndpointKey(request);
        var state = monitor.GetBucket(bucketKey);
        var now = DateTimeOffset.UtcNow;

        if (state is not null && state.IsErrorLimited(now))
        {
            logger.LogWarning("ESI error-limit gate: short-circuiting {Bucket} until reset (420 active).", bucketKey);
            return SyntheticErrorLimited(state, now);
        }

        if (state is not null)
        {
            var delay = state.PreemptiveDelay(now);
            if (delay > TimeSpan.Zero)
            {
                logger.LogWarning("ESI rate-limit gate: throttling {Bucket} for {Delay}ms (low remaining).", bucketKey, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        var response = await base.SendAsync(request, cancellationToken);

        monitor.RecordBucket(bucketKey, endpointKey, EsiRateLimitHeaders.Parse(response), (int)response.StatusCode);
        monitor.RecordCache(bucketKey, endpointKey, ReadCacheStatus(response));
        return response;
    }

    // ESI reports its edge-cache outcome per call in this response header (HIT/MISS/EXPIRED/…).
    private static string? ReadCacheStatus(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Esi-Cache-Status", out var values) ? values.FirstOrDefault() : null;

    private static HttpResponseMessage SyntheticErrorLimited(EsiBucketState state, DateTimeOffset now)
    {
        var response = new HttpResponseMessage((HttpStatusCode)420)
        {
            Content = new StringContent("{\"error\":\"Error limit reached — call withheld by the local gate.\"}", Encoding.UTF8, "application/json")
        };
        if (state.ErrorResetAt is { } reset)
        {
            var seconds = (int)Math.Ceiling((reset - now).TotalSeconds);
            response.Headers.TryAddWithoutValidation("X-ESI-Error-Limit-Reset", Math.Max(seconds, 0).ToString());
            response.Headers.TryAddWithoutValidation("X-ESI-Error-Limit-Remain", "0");
        }
        return response;
    }
}
