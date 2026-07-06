using System.Collections.Concurrent;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// Thread-safe <see cref="IEsiRateLimitMonitor"/>. Keeps a live <see cref="EsiBucketState"/> per
/// bucket and warns when either system runs low. Each bucket is mutated under its own lock so the gate and
/// the metrics window read a consistent state.
/// </summary>
public sealed class EsiRateLimitMonitor(ILogger<EsiRateLimitMonitor> logger) : IEsiRateLimitMonitor, ISingletonService
{
    private const int ErrorWarningThreshold = 20;
    private const int BucketWarningThreshold = 5;

    private readonly ConcurrentDictionary<string, EsiBucketState> _buckets = new();

    private volatile EsiRateLimitState? _current;

    public EsiRateLimitState? Current => _current;

    public event Action<EsiRateLimitState> StateChanged = _ => { };

    public IReadOnlyCollection<EsiBucketState> Buckets => _buckets.Values.ToArray();

    public EsiBucketState? GetBucket(string bucketKey) =>
        _buckets.TryGetValue(bucketKey, out var state) ? state : null;

    public void Record(int? errorRemaining, DateTimeOffset? resetAt)
    {
        if (errorRemaining is null) return;

        var state = new EsiRateLimitState(errorRemaining.Value, resetAt ?? DateTimeOffset.UtcNow.AddSeconds(60));
        _current = state;

        if (state.ErrorRemaining <= ErrorWarningThreshold)
            logger.LogError(
                "ESI error limit low: {Remaining} errors remaining (resets at {ResetAt:HH:mm:ss} UTC). Throttling calls.",
                state.ErrorRemaining, state.ResetAt);

        try { StateChanged(state); }
        catch { /* subscribers must not crash */ }
    }

    public void RecordBucket(string bucketKey, string endpointKey, EsiRateLimitHeaders headers, int statusCode)
    {
        var bucket = _buckets.GetOrAdd(bucketKey, static key => new EsiBucketState(key));
        var endpoint = bucket.Endpoints.GetOrAdd(endpointKey, static key => new EsiEndpointState(key));
        var now = DateTimeOffset.UtcNow;

        lock (bucket)
        {
            bucket.Calls++;
            if (statusCode is >= 200 and < 400) bucket.Successes++; else bucket.Failures++;
            if (statusCode == 420) bucket.ErrorLimitHits++;
            if (statusCode == 429) bucket.BucketHits++;
            bucket.LastStatus = statusCode;
            bucket.UpdatedAt = now;

            if (headers.ErrorRemaining is not null) bucket.ErrorRemaining = headers.ErrorRemaining;
            if (headers.ErrorResetAt is not null) bucket.ErrorResetAt = headers.ErrorResetAt;
            if (headers.BucketGroup is not null) bucket.BucketGroup = headers.BucketGroup;
            if (headers.BucketLimit is not null) bucket.BucketLimit = headers.BucketLimit;
            if (headers.BucketRemaining is not null) bucket.BucketRemaining = headers.BucketRemaining;
            if (headers.BucketUsed is not null) bucket.BucketUsed = headers.BucketUsed;
            if (statusCode == 429 && headers.RetryAfter is { } retryAfter)
                bucket.BucketBlockedUntil = DateTimeOffset.UtcNow.Add(retryAfter);

            // Same counters against the endpoint dimension (under the bucket lock for a consistent snapshot).
            endpoint.Calls++;
            if (statusCode is >= 200 and < 400) endpoint.Successes++; else endpoint.Failures++;
            if (statusCode == 420) endpoint.ErrorLimitHits++;
            if (statusCode == 429) endpoint.BucketHits++;
            if (headers.BucketGroup is not null) endpoint.BucketGroup = headers.BucketGroup;
            endpoint.LastStatus = statusCode;
            endpoint.UpdatedAt = now;
        }

        // Keep the legacy global error-limit state alive for any existing consumer.
        if (headers.ErrorRemaining is not null)
            Record(headers.ErrorRemaining, headers.ErrorResetAt);

        WarnIfLow(bucket, statusCode);
    }

    public void RecordCache(string bucketKey, string endpointKey, string? cacheStatus)
    {
        if (string.IsNullOrWhiteSpace(cacheStatus)) return;

        var bucket = _buckets.GetOrAdd(bucketKey, static key => new EsiBucketState(key));
        var endpoint = bucket.Endpoints.GetOrAdd(endpointKey, static key => new EsiEndpointState(key));
        var isHit = string.Equals(cacheStatus, "HIT", StringComparison.OrdinalIgnoreCase);
        var isMiss = string.Equals(cacheStatus, "MISS", StringComparison.OrdinalIgnoreCase);

        lock (bucket)
        {
            if (isHit) bucket.CacheHits++;
            else if (isMiss) bucket.CacheMisses++;
            bucket.LastCacheStatus = cacheStatus;
            bucket.UpdatedAt = DateTimeOffset.UtcNow;

            if (isHit) endpoint.CacheHits++;
            else if (isMiss) endpoint.CacheMisses++;
            endpoint.LastCacheStatus = cacheStatus;
        }
    }

    public void RecordLocalCacheHit(string bucketKey, string endpointKey)
    {
        var bucket = _buckets.GetOrAdd(bucketKey, static key => new EsiBucketState(key));
        var endpoint = bucket.Endpoints.GetOrAdd(endpointKey, static key => new EsiEndpointState(key));
        var now = DateTimeOffset.UtcNow;

        lock (bucket)
        {
            // A local hit is a successful call the app made, just served without a socket — count it as such, but
            // leave the rate-limit headroom (420/429) alone since no ESI headers came back.
            bucket.Calls++;
            bucket.Successes++;
            bucket.LocalCacheHits++;
            bucket.LastStatus = 200;
            bucket.UpdatedAt = now;

            endpoint.Calls++;
            endpoint.Successes++;
            endpoint.LocalCacheHits++;
            endpoint.LastStatus = 200;
            endpoint.UpdatedAt = now;
        }
    }

    private void WarnIfLow(EsiBucketState bucket, int statusCode)
    {
        if (statusCode == 420 || bucket.ErrorRemaining is <= ErrorWarningThreshold and > 0)
            logger.LogWarning("ESI error limit low on bucket {Bucket}: {Remaining} left.", bucket.Key, bucket.ErrorRemaining);
        if (statusCode == 429 || bucket.BucketRemaining is <= BucketWarningThreshold and > 0)
            logger.LogWarning("ESI bucket limit low on bucket {Bucket}: {Remaining} left.", bucket.Key, bucket.BucketRemaining);
    }
}
