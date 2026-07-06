using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ESI CDN cache metrics: the rate-limit handler reads ESI's <c>X-Esi-Cache-Status</c>
/// response header and feeds HIT/MISS counts (plus the last raw value) per bucket into the metrics monitor,
/// which the metrics window surfaces.
/// </summary>
public class EsiCacheMetricsTests
{
    [Fact]
    public void RecordCache_CountsHitsAndMisses_AndKeepsLastStatus()
    {
        var monitor = new EsiRateLimitMonitor(NullLogger<EsiRateLimitMonitor>.Instance);

        monitor.RecordCache("ip", "/status/", "HIT");
        monitor.RecordCache("ip", "/status/", "hit");   // case-insensitive
        monitor.RecordCache("ip", "/status/", "HIT");
        monitor.RecordCache("ip", "/status/", "MISS");

        var bucket = monitor.GetBucket("ip");
        Assert.NotNull(bucket);
        Assert.Equal(3, bucket!.CacheHits);
        Assert.Equal(1, bucket.CacheMisses);
        Assert.Equal("MISS", bucket.LastCacheStatus);

        // The same counts roll up under the endpoint dimension.
        var endpoint = bucket.Endpoints["/status/"];
        Assert.Equal(3, endpoint.CacheHits);
        Assert.Equal(1, endpoint.CacheMisses);
    }

    [Fact]
    public void RecordCache_IgnoresAbsentHeader_AndDoesNotBucketOtherStatuses()
    {
        var monitor = new EsiRateLimitMonitor(NullLogger<EsiRateLimitMonitor>.Instance);

        monitor.RecordCache("ip", "/status/", null);   // header absent → no bucket created
        Assert.Null(monitor.GetBucket("ip"));

        monitor.RecordCache("ip", "/status/", "EXPIRED"); // visible as last status, but not a HIT or a MISS
        var bucket = monitor.GetBucket("ip");
        Assert.NotNull(bucket);
        Assert.Equal(0, bucket!.CacheHits);
        Assert.Equal(0, bucket.CacheMisses);
        Assert.Equal("EXPIRED", bucket.LastCacheStatus);
    }

    [Fact]
    public void RowViewModel_ShowsCacheCountsAndHitRate()
    {
        var bucket = new EsiBucketState("ip") { CacheHits = 3, CacheMisses = 1, LastCacheStatus = "MISS" };

        var row = new EsiBucketRowViewModel(bucket);

        Assert.Equal("3/1", row.CacheText);
        Assert.Contains("75% HIT", row.CacheTooltip);
        Assert.Contains("last: MISS", row.CacheTooltip);
    }

    [Theory]
    [InlineData("HIT", 1, 0)]
    [InlineData("MISS", 0, 1)]
    public async Task RateLimitHandler_RecordsEsiCacheStatus_FromResponseHeader(string status, int expectedHits, int expectedMisses)
    {
        var monitor = new EsiRateLimitMonitor(NullLogger<EsiRateLimitMonitor>.Instance);
        var handler = new EsiRateLimitHandler(monitor, NullLogger<EsiRateLimitHandler>.Instance)
        {
            InnerHandler = new CacheStatusStub(status)
        };
        using var invoker = new HttpMessageInvoker(handler);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://esi.evetech.net/status/");
        request.Options.Set(EsiRequestOptions.BucketKey, "ip");
        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        var bucket = monitor.GetBucket("ip");
        Assert.NotNull(bucket);
        Assert.Equal(expectedHits, bucket!.CacheHits);
        Assert.Equal(expectedMisses, bucket.CacheMisses);
        Assert.Equal(status, bucket.LastCacheStatus);
    }

    [Fact]
    public void RecordLocalCacheHit_CountsCall_WithoutTouchingHeadroom()
    {
        var monitor = new EsiRateLimitMonitor(NullLogger<EsiRateLimitMonitor>.Instance);

        monitor.RecordLocalCacheHit("app:123", "/characters/{id}/");
        monitor.RecordLocalCacheHit("app:123", "/characters/{id}/");

        var bucket = monitor.GetBucket("app:123");
        Assert.NotNull(bucket);
        Assert.Equal(2, bucket!.Calls);
        Assert.Equal(2, bucket.Successes);
        Assert.Equal(2, bucket.LocalCacheHits);
        Assert.Null(bucket.ErrorRemaining);   // a local hit carries no ESI headers → headroom untouched
        Assert.Equal(2, bucket.Endpoints["/characters/{id}/"].LocalCacheHits);
    }

    [Fact]
    public async Task CacheHandler_FreshLocalHit_RecordsLocalHit_WithoutNetwork()
    {
        var monitor = new EsiRateLimitMonitor(NullLogger<EsiRateLimitMonitor>.Instance);
        var inner = new CountingStub();
        var handler = new EsiCacheHandler(new FreshStubStore(), monitor) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://esi.evetech.net/characters/123/");
        request.Options.Set(EsiRequestOptions.BucketKey, "app:123");
        request.Options.Set(EsiRequestOptions.EndpointKey, "/characters/{id}/");
        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(0, inner.Calls);   // served entirely from the local cache — no socket opened
        var bucket = monitor.GetBucket("app:123");
        Assert.NotNull(bucket);
        Assert.Equal(1, bucket!.LocalCacheHits);
        Assert.Equal(1, bucket.Endpoints["/characters/{id}/"].LocalCacheHits);
    }

    /// <summary>Inner handler that returns a 200 carrying a chosen <c>X-Esi-Cache-Status</c> header.</summary>
    private sealed class CacheStatusStub(string? cacheStatus) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            if (cacheStatus is not null)
                response.Headers.TryAddWithoutValidation("X-Esi-Cache-Status", cacheStatus);
            return Task.FromResult(response);
        }
    }

    /// <summary>A store that always returns a fresh entry, so the cache handler short-circuits before the network.</summary>
    private sealed class FreshStubStore : IEsiCacheStore
    {
        public Task<EsiCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<EsiCacheEntry?>(new EsiCacheEntry("{}", null, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow));

        public Task SetAsync(string key, EsiCacheEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    /// <summary>Inner handler that records whether it was hit — it must not be, on a fresh local cache hit.</summary>
    private sealed class CountingStub : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
