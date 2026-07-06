using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Cache handler (Deel 3), sitting outside the rate-limit + retry handlers so a hit costs no
/// socket call and no rate-limit budget. Fresh GETs short-circuit from the store; stale entries with an
/// ETag revalidate via <c>If-None-Match</c> and reuse the stored body on 304. New 200s are stored with a
/// TTL from <c>Expires</c> (1h fallback), forever for immutable resources (killmails), ± a 10% jitter to
/// avoid synchronised expiry across characters.
/// </summary>
public sealed class EsiCacheHandler(IEsiCacheStore store, IEsiRateLimitMonitor monitor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is null)
            return await base.SendAsync(request, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var key = FileEsiCacheStore.KeyFor(request.RequestUri.AbsoluteUri);
        var cached = await store.GetAsync(key, cancellationToken);

        if (cached is not null && cached.IsFresh(now))
        {
            // A fresh local hit short-circuits before the rate-limit handler — record it here or it stays invisible.
            monitor.RecordLocalCacheHit(EsiRequestOptions.ResolveBucketKey(request), EsiRequestOptions.ResolveEndpointKey(request));
            return FromCache(cached);
        }

        if (cached?.ETag is { } etag)
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            var refreshed = cached with { ExpiresAt = ComputeExpiry(response, request.RequestUri, now), StoredAt = now };
            await store.SetAsync(key, refreshed, cancellationToken);
            response.Dispose();
            return FromCache(refreshed);
        }

        if (response.StatusCode == HttpStatusCode.OK)
        {
            await response.Content.LoadIntoBufferAsync();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var entry = new EsiCacheEntry(body, StrongETag(response), ComputeExpiry(response, request.RequestUri, now), now);
            await store.SetAsync(key, entry, cancellationToken);
        }

        return response;
    }

    private static HttpResponseMessage FromCache(EsiCacheEntry entry)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(entry.Body, Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation(EsiCacheHeaders.FromCache, "1");
        if (entry.ExpiresAt is { } expires)
            response.Content.Headers.Expires = expires;
        return response;
    }

    /// <summary>Expires primary, 1h fallback, forever for immutables, ± 10% jitter.</summary>
    private static DateTimeOffset? ComputeExpiry(HttpResponseMessage response, Uri uri, DateTimeOffset now)
    {
        if (IsImmutable(uri))
            return null;

        var ttl = response.Content.Headers.Expires is { } expires
            ? expires - now
            : TimeSpan.FromHours(1);
        if (ttl <= TimeSpan.Zero)
            ttl = TimeSpan.FromSeconds(1);

        var jitter = 1 + (Random.Shared.NextDouble() - 0.5) / 5; // ±10%
        return now + ttl * jitter;
    }

    // Killmails (and statics derived from them) never change → effectively forever (§1, ~356d TTL).
    private static bool IsImmutable(Uri uri) => uri.AbsolutePath.Contains("/killmails/", StringComparison.Ordinal);

    // Weak validators (W/) must not be used as strong ETags (§5).
    private static string? StrongETag(HttpResponseMessage response) =>
        response.Headers.ETag is { IsWeak: false, Tag: { Length: > 0 } tag } ? tag : null;
}
