using System.Collections.Concurrent;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Live per-bucket rate-limit state: both ESI systems plus call/hit counters. The bucket key
/// is <c>app:characterId</c> for authed calls and <c>ip</c> for public calls (§4b). Mutated under the
/// monitor's lock; this is also the data layer the ESI-metrics window reads.
/// </summary>
public sealed class EsiBucketState(string key)
{
    private const int ErrorWarnThreshold = 20;
    private const int BucketWarnThreshold = 5;
    private static readonly TimeSpan MaxPreemptiveDelay = TimeSpan.FromSeconds(2);

    public string Key { get; } = key;

    // Error-limit system (420, §4a).
    public int? ErrorRemaining { get; set; }
    public DateTimeOffset? ErrorResetAt { get; set; }

    // Bucket/token system (429, §4b).
    public string? BucketGroup { get; set; }
    public int? BucketLimit { get; set; }
    public int? BucketRemaining { get; set; }
    public int? BucketUsed { get; set; }
    public DateTimeOffset? BucketBlockedUntil { get; set; }

    // Counters for the metrics feed.
    public long Calls { get; set; }
    public long Successes { get; set; }
    public long Failures { get; set; }
    public long ErrorLimitHits { get; set; }
    public long BucketHits { get; set; }

    // ESI CDN cache effectiveness, read from the response's X-Esi-Cache-Status header (HIT/MISS/EXPIRED/…).
    // HIT = CCP served it from their edge cache; MISS = CCP regenerated it. Only counted on network calls
    // (a response served from our own local store never carries the header). LastCacheStatus keeps the raw
    // value (so EXPIRED/PASS stay visible even though they are not bucketed into hit/miss).
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public string? LastCacheStatus { get; set; }

    // Calls served entirely from our own local file cache — they short-circuit before the network (and before
    // the rate-limit handler), so they would otherwise be invisible in the metrics. Counted so the window shows
    // that a call happened even when no socket was opened (e.g. the on-startup affiliation refresh within TTL).
    public long LocalCacheHits { get; set; }

    public int LastStatus { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Per-endpoint counters within this bucket, keyed by route template (e.g. <c>/characters/{id}/</c>). The
    /// second metrics dimension (the metrics window groups by bucket, then drills into these). A concurrent map
    /// so the window can enumerate it lock-free while the monitor mutates an entry under the bucket lock.
    /// </summary>
    public ConcurrentDictionary<string, EsiEndpointState> Endpoints { get; } = new();

    /// <summary>True while the error limit is exhausted and its window has not reset — a hard stop.</summary>
    public bool IsErrorLimited(DateTimeOffset now) =>
        ErrorRemaining is <= 0 && ErrorResetAt is { } reset && reset > now;

    /// <summary>
    /// Pre-emptive throttle (CCP best-practice §4): when the error-limit remaining is low, spread the few
    /// remaining calls across the reset window instead of racing into a 420. Zero when there is headroom.
    /// </summary>
    public TimeSpan PreemptiveDelay(DateTimeOffset now)
    {
        if (ErrorRemaining is > 0 and <= ErrorWarnThreshold && ErrorResetAt is { } reset && reset > now)
        {
            var spread = TimeSpan.FromTicks((reset - now).Ticks / Math.Max(ErrorRemaining.Value, 1));
            return spread < MaxPreemptiveDelay ? spread : MaxPreemptiveDelay;
        }

        if (BucketRemaining is > 0 and <= BucketWarnThreshold)
            return TimeSpan.FromMilliseconds(250);

        return TimeSpan.Zero;
    }
}
