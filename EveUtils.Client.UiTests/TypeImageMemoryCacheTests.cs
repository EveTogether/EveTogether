using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class TypeImageMemoryCacheTests
{
    [AvaloniaFact]
    public async Task GetImage_ThirtyThree512Renders_EvictsOldestAndReloadsIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eveutils-img-" + Guid.NewGuid().ToString("N"));
        var imagePath = Path.Combine(directory, "image.png");

        try
        {
            Directory.CreateDirectory(directory);
            byte[] image = _Create512Image(imagePath);
            var handler = new CountingHandler(image);
            var provider = new TypeImageProvider(new StubHttpClientFactory(handler), new EmptySettings(), directory);

            for (var typeId = 1; typeId <= 33; typeId++)
                await provider.GetImageAsync(typeId, TypeImageKind.Render, 512, TestContext.Current.CancellationToken);

            Assert.Equal(33, handler.Calls);
            Assert.Equal(32, _CacheCount(provider));

            File.Delete(Path.Combine(directory, "type-images", "1_Render_512.png"));
            await provider.GetImageAsync(1, TypeImageKind.Render, 512, TestContext.Current.CancellationToken);

            Assert.Equal(34, handler.Calls);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] _Create512Image(string imagePath)
    {
        using var image = new RenderTargetBitmap(new PixelSize(512, 512), new Vector(96, 96));
        image.Save(imagePath, PngBitmapEncoderOptions.Default);
        return File.ReadAllBytes(imagePath);
    }

    private static int _CacheCount(TypeImageProvider provider)
    {
        object cache = typeof(TypeImageProvider).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider)
            ?? throw new InvalidOperationException("Type image memory cache is unavailable");
        return (int)(cache.GetType().GetProperty("Count")?.GetValue(cache)
            ?? throw new InvalidOperationException("Type image memory cache count is unavailable"));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://images.evetech.net/") };
    }

    private sealed class EmptySettings : ISettingRepository
    {
        public Task<IReadOnlyList<ClientSetting>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClientSetting>>([]);

        public Task<ClientSetting?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<ClientSetting?>(null);

        public Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CountingHandler(byte[] image) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) });
        }
    }
}
