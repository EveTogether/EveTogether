namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Live per-endpoint call counters within a bucket — the second metrics dimension (the route template, e.g.
/// <c>/characters/{id}/</c>, from <see cref="EsiEndpointKey"/>). Holds only counters, not rate-limit headroom:
/// the 420/429 budgets are accounted per bucket, so headroom stays on <see cref="EsiBucketState"/>. Mutated
/// under the owning bucket's lock; read lock-free by the metrics window (eventually-consistent, like the bucket).
/// </summary>
public sealed class EsiEndpointState(string endpoint)
{
    public string Endpoint { get; } = endpoint;

    /// <summary>ESI's reported rate-limit group for this route (<c>X-Ratelimit-Group</c>), when present.</summary>
    public string? BucketGroup { get; set; }

    public long Calls { get; set; }
    public long Successes { get; set; }
    public long Failures { get; set; }
    public long ErrorLimitHits { get; set; }
    public long BucketHits { get; set; }

    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public long LocalCacheHits { get; set; }
    public string? LastCacheStatus { get; set; }

    public int LastStatus { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
