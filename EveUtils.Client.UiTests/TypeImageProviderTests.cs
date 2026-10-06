using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The type-image provider collapses a request stampede (concurrent callers for the same image) to a single download
/// and serves repeat requests from the disk cache, so opening fits stays fast and offline-capable. The caching
/// behaviour is observed via the network-call count and the on-disk cache file — the bitmap decode itself is platform
/// rendering, not exercised here.
/// </summary>
public class TypeImageProviderTests
{
    [Fact]
    public async Task GetImage_ConcurrentRequests_DownloadOnce_ThenServeFromDiskOnAFreshProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eveutils-img-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new CountingHandler();
            var provider = new TypeImageProvider(new StubHttpClientFactory(handler), new EmptySettings(), dir);

            // Two concurrent callers for the same type/kind/size must share one in-flight download.
            await Task.WhenAll(
                provider.GetImageAsync(587, TypeImageKind.Icon, 64, TestContext.Current.CancellationToken),
                provider.GetImageAsync(587, TypeImageKind.Icon, 64, TestContext.Current.CancellationToken));

            Assert.Equal(1, handler.Calls);   // stampede collapsed to a single network fetch
            Assert.True(File.Exists(Path.Combine(dir, "type-images", "587_Icon_64.png")));   // written to the disk cache

            // A fresh provider over the same data dir serves the cached file with no further download.
            var coldHandler = new CountingHandler();
            var coldProvider = new TypeImageProvider(new StubHttpClientFactory(coldHandler), new EmptySettings(), dir);
            await coldProvider.GetImageAsync(587, TypeImageKind.Icon, 64, TestContext.Current.CancellationToken);
            Assert.Equal(0, coldHandler.Calls);   // read from the disk cache, not the network
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetImage_FailedDownload_IsNotCached_SoTheNextRequestRetries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eveutils-img-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new FlakyHandler();   // first call throws, then succeeds
            var provider = new TypeImageProvider(new StubHttpClientFactory(handler), new EmptySettings(), dir);

            await provider.GetImageAsync(587, TypeImageKind.Icon, 64, TestContext.Current.CancellationToken);   // failure: not cached, no file
            Assert.False(File.Exists(Path.Combine(dir, "type-images", "587_Icon_64.png")));

            await provider.GetImageAsync(587, TypeImageKind.Icon, 64, TestContext.Current.CancellationToken);   // retried -> downloads again
            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetImage_NotFound_ReturnsNull_AndIsNotRequestedAgainThisSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eveutils-img-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new NotFoundHandler();
            var provider = new TypeImageProvider(new StubHttpClientFactory(handler), new EmptySettings(), dir);

            Assert.Null(await provider.GetImageAsync(43519, TypeImageKind.Icon, 32, TestContext.Current.CancellationToken));
            Assert.Null(await provider.GetImageAsync(43519, TypeImageKind.Icon, 32, TestContext.Current.CancellationToken));

            Assert.Equal(1, handler.Calls);
            Assert.False(File.Exists(Path.Combine(dir, "type-images", "43519_Icon_32.png")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task GetImage_SkinType_ReturnsBundledPlaceholder_WithoutTheNetwork()
    {
        var handler = new CountingHandler();
        var provider = new TypeImageProvider(new StubHttpClientFactory(handler), new EmptySettings(), "unused",
            new CategoryOnlyDogma(91));

        Bitmap? first = await provider.GetImageAsync(43519, TypeImageKind.Icon, 32, TestContext.Current.CancellationToken);
        Bitmap? second = await provider.GetImageAsync(43520, TypeImageKind.Icon, 64, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(0, handler.Calls);
    }

    [AvaloniaFact]
    public async Task GetImage_OrdinaryType_StillComesFromTheImageServer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eveutils-img-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new CountingHandler();
            var provider = new TypeImageProvider(new StubHttpClientFactory(handler), new EmptySettings(), dir,
                new CategoryOnlyDogma(7));

            Bitmap? image = await provider.GetImageAsync(587, TypeImageKind.Icon, 32, TestContext.Current.CancellationToken);

            Assert.NotNull(image);
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAMUlEQVR4nBXIMREAMBDDsAALsB8NKvzcq0YlwQYvuGBSbPGK64/DHt7h7sewwxtu+AA5yCMxCGZNswAAAABJRU5ErkJggg==");

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://images.evetech.net/") };
    }

    private sealed class EmptySettings : ISettingRepository
    {
        public Task<IReadOnlyList<ClientSetting>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClientSetting>>([]);
        public Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(40, cancellationToken);   // hold the request so concurrent callers overlap in-flight
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PngBytes) };
        }
    }

    private sealed class FlakyHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new HttpRequestException("simulated offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PngBytes) });
        }
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class CategoryOnlyDogma(int categoryId) : IDogmaDataAccessor
    {
        public int? GetCategoryId(int typeId) => categoryId;

        public DogmaAttributeMeta? GetAttributeMeta(int attributeId) => throw new NotSupportedException();
        public IReadOnlyList<SdeDogmaAttribute> GetBaseAttributes(int typeId) => throw new NotSupportedException();
        public IReadOnlyList<DogmaTypeEffect> GetTypeEffects(int typeId) => throw new NotSupportedException();
        public DogmaEffectDef? GetEffect(int effectId) => throw new NotSupportedException();
        public int? GetGroupId(int typeId) => throw new NotSupportedException();
        public IReadOnlyList<int> GetSkillTypeIds() => throw new NotSupportedException();
        public double? GetMass(int typeId) => throw new NotSupportedException();
        public int? GetDefaultTacticalModeTypeId(int shipTypeId) => throw new NotSupportedException();
        public IReadOnlyList<SdeNamedType> GetTacticalModes(int shipTypeId) => throw new NotSupportedException();
        public double? GetCapacity(int typeId) => throw new NotSupportedException();
        public double? GetVolume(int typeId) => throw new NotSupportedException();
        public Task PrefetchAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public void Reopen() => throw new NotSupportedException();
    }
}
