using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Innermost ESI handler (Deel 5): the retry/backoff/fallback matrix. No retry on 4xx — those (incl. the routine
/// 404 "not in a fleet") are logged once by the pivot (EsiClient) with their body and mapped onto EsiErrorKind. The
/// <c>/status/</c> poll is never retried either: a failed status poll is itself the "ESI is down" signal, so retrying
/// it just bursts a dead API every second — the poller's own interval is the cadence. 420 stops; 429 waits the real
/// <c>Retry-After</c>; 5xx/504/transient failures get the bounded backoff schedule (<see cref="EsiRetryPolicy"/>) with
/// jitter. A 5xx is logged here only when it will actually be retried — the final one surfaces to the pivot, which logs
/// it once, so we never double-line it. Every response's content is buffered so the cache handler and the pivot can
/// re-read it. Always <c>await Task.Delay</c>, never <c>Thread.Sleep</c>.
/// </summary>
public sealed class EsiRetryHandler(EsiRetryPolicy policy, IEsiOutageDetector outageDetector, ILogger<EsiRetryHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
            await request.Content.LoadIntoBufferAsync();

        // No retries for the status poll (a failed poll is the detection signal itself), nor once a run of failures
        // already suggests ESI is down — piling attempts on a likely-dead API just hammers it; surface it at once.
        var maxRetries = EsiStatusEndpoint.IsStatusPoll(request.RequestUri) || outageDetector.IsSuspect ? 0 : policy.MaxRetries;

        for (var attempt = 0; ; attempt++)
        {
            using var attemptRequest = await CloneAsync(request);

            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(attemptRequest, cancellationToken);
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken) && attempt < maxRetries)
            {
                logger.LogWarning(ex, "ESI {Method} {Uri} transport error (attempt {Attempt}). Retrying.", request.Method, request.RequestUri, attempt + 1);
                await BackoffAsync(attempt, cancellationToken);
                continue;
            }

            // Buffer so the error body can be logged here and re-read by the cache handler + pivot.
            await response.Content.LoadIntoBufferAsync();

            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode || status == 304)
                return response;

            if (status == 420)
                return response; // error-limited — stop, no retry (the gate withholds further calls)

            if (status == 429 && attempt < maxRetries)
            {
                var wait = Clamp(response.Headers.RetryAfter?.Delta ?? policy.DelayFor(attempt));
                logger.LogWarning("ESI 429 on {Uri} (attempt {Attempt}). Waiting {Delay}s (Retry-After).", request.RequestUri, attempt + 1, wait.TotalSeconds);
                response.Dispose();
                await Task.Delay(wait, cancellationToken);
                continue;
            }

            if (status >= 500 && attempt < maxRetries)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("ESI {Method} {Uri} failed: {Status} (attempt {Attempt}). Retrying. Body: {Body}", request.Method, request.RequestUri, status, attempt + 1, Trim(body));
                response.Dispose();
                await BackoffAsync(attempt, cancellationToken);
                continue;
            }

            return response; // 4xx, a no-retry status poll, or retries exhausted — surface to the pivot
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        ex is HttpRequestException or TaskCanceledException;

    private Task BackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        var jitter = 1 + (Random.Shared.NextDouble() - 0.5) / 5; // ±10% so concurrent retries don't lockstep
        return Task.Delay(Clamp(policy.DelayFor(attempt) * jitter), cancellationToken);
    }

    private TimeSpan Clamp(TimeSpan delay) => delay < policy.MaxDelay ? delay : policy.MaxDelay;

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in (IDictionary<string, object?>)request.Options)
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync();
            var content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            clone.Content = content;
        }

        return clone;
    }

    private static string Trim(string body) => body.Length <= 500 ? body : body[..500];
}
