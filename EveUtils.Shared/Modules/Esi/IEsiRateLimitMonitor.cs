using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// Tracks ESI's two rate-limit systems per bucket: the 420 error-limit and the 429 token
/// bucket, keyed by <c>app:characterId</c> / <c>ip</c> (§4b). The rate-limit handler feeds it after every
/// call; consumers (the gate, the ESI-metrics window) read it. The legacy single-state members are
/// retained for the existing error-limit consumers.
/// </summary>
public interface IEsiRateLimitMonitor
{
    /// <summary>Legacy global error-limit snapshot; null if no ESI call has been observed yet.</summary>
    EsiRateLimitState? Current { get; }

    /// <summary>Legacy error-limit record (used by the older fittings client until it routes the pivot).</summary>
    void Record(int? errorRemaining, DateTimeOffset? resetAt);

    /// <summary>Raised when a new global error-limit state is recorded (on the calling thread).</summary>
    event Action<EsiRateLimitState> StateChanged;

    /// <summary>
    /// Records both systems' headers + the status for a bucket after an ESI call, and the same counters
    /// against the call's endpoint route template (<paramref name="endpointKey"/>, e.g. <c>/characters/{id}/</c>)
    /// so the metrics window can drill from a bucket into its per-endpoint breakdown.
    /// </summary>
    void RecordBucket(string bucketKey, string endpointKey, EsiRateLimitHeaders headers, int statusCode);

    /// <summary>
    /// Records ESI's reported CDN cache status for a bucket (and its endpoint) from the response's
    /// <c>X-Esi-Cache-Status</c> header (HIT/MISS/EXPIRED/…). A null/empty value (header absent) is ignored.
    /// </summary>
    void RecordCache(string bucketKey, string endpointKey, string? cacheStatus);

    /// <summary>
    /// Records a call that was served entirely from the local file cache (a fresh-cache short-circuit, before the
    /// network and before this handler). Counts a successful call against the bucket + endpoint without touching
    /// rate-limit headroom (a local hit carries no ESI headers), so the metrics show the call happened locally.
    /// </summary>
    void RecordLocalCacheHit(string bucketKey, string endpointKey);

    /// <summary>Current state for a bucket, or null if it has never been seen.</summary>
    EsiBucketState? GetBucket(string bucketKey);

    /// <summary>Snapshot of all tracked buckets (the metrics data layer).</summary>
    IReadOnlyCollection<EsiBucketState> Buckets { get; }
}
