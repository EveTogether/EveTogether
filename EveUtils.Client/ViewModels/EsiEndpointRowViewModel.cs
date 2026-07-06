using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One endpoint row inside a bucket's accordion in the ESI-metrics window: a display snapshot of an
/// <see cref="EsiEndpointState"/> — the route template (e.g. <c>/characters/{id}/</c>) plus its call/error/cache
/// counters. Rate-limit headroom is not shown here: the 420/429 budgets are per bucket, not per endpoint.
/// </summary>
public sealed class EsiEndpointRowViewModel(EsiEndpointState endpoint)
{
    public string Endpoint { get; } = endpoint.Endpoint;
    public long Calls { get; } = endpoint.Calls;
    public long Successes { get; } = endpoint.Successes;
    public long Failures { get; } = endpoint.Failures;
    public bool HasFailures { get; } = endpoint.Failures > 0;

    public string OkFailText { get; } = $"{endpoint.Successes}/{endpoint.Failures}";

    public string ErrorRateText { get; } =
        endpoint.Calls == 0 ? "—" : $"{100.0 * endpoint.Failures / endpoint.Calls:0.#}%";

    /// <summary>ESI CDN cache HIT/MISS (X-Esi-Cache-Status), plus a "· Nl" suffix for calls served from our local file cache.</summary>
    public string CacheText { get; } =
        endpoint.LocalCacheHits > 0
            ? $"{endpoint.CacheHits}/{endpoint.CacheMisses} · {endpoint.LocalCacheHits}l"
            : $"{endpoint.CacheHits}/{endpoint.CacheMisses}";

    public string CacheTooltip { get; } =
        (endpoint.CacheHits + endpoint.CacheMisses == 0
            ? "no ESI CDN cache status seen yet"
            : $"{100.0 * endpoint.CacheHits / (endpoint.CacheHits + endpoint.CacheMisses):0.#}% HIT (X-Esi-Cache-Status)")
        + (endpoint.LocalCacheHits > 0 ? $" · {endpoint.LocalCacheHits} served from local file cache (no network)" : "");

    /// <summary>ESI's rate-limit group for this route (X-Ratelimit-Group), or "—" when ESI sent none.</summary>
    public string GroupText { get; } = string.IsNullOrEmpty(endpoint.BucketGroup) ? "—" : endpoint.BucketGroup;

    public int LastStatus { get; } = endpoint.LastStatus;
}
