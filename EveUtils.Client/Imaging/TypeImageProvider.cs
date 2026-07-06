using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using EveUtils.Shared.Modules.Settings.Repositories;

namespace EveUtils.Client.Imaging;

/// <summary>
/// <see cref="ITypeImageProvider"/> backed by the CCP image server (<c>images.evetech.net</c>), gated behind the
/// opt-in setting. Each image is fetched at most once and renders offline afterwards: the <b>in-flight task</b>
/// is memoised, so concurrent requests for the same image (e.g. eight identical turrets, or the same hull in the browser
/// and the detail window) share a single download + decode instead of stampeding the server. The bytes are written to a
/// per-instance disk cache and the decode runs off the UI thread. Any failure (offline, disabled, 404) yields null — and
/// is not cached, so the next request retries — and the wheel falls back to its offline glyph.
/// </summary>
public sealed class TypeImageProvider(IHttpClientFactory httpClientFactory, ISettingRepository settings, string dataDirectory)
    : ITypeImageProvider
{
    public const string HttpClientName = "evetech-images";
    public const string EnabledSettingKey = "fit.images.enabled";

    private readonly string _cacheDirectory = Path.Combine(dataDirectory, "type-images");
    // Memoise the load task (not just the finished bitmap), so N callers for one key await one download. Wrapped in a
    // Lazy so the load runs exactly once even when GetOrAdd's factory races under concurrent access.
    private readonly ConcurrentDictionary<string, Lazy<Task<Bitmap?>>> _cache = new();

    public async Task<bool> AreImagesEnabledAsync(CancellationToken cancellationToken = default)
    {
        // Default on: images load unless the user has explicitly switched them off.
        foreach (var setting in await settings.ListAsync(cancellationToken))
            if (setting.Key == EnabledSettingKey)
                return !string.Equals(setting.Value, "false", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    public Task<Bitmap?> GetImageAsync(int typeId, TypeImageKind kind, int size,
        CancellationToken cancellationToken = default)
    {
        // The load is shared across callers, so a single caller's cancellation must not abort it for the others — the
        // per-call token is intentionally not threaded into the shared download (image loads are fire-and-forget).
        var key = $"{typeId}_{kind}_{size}";
        return _cache.GetOrAdd(key, k => new Lazy<Task<Bitmap?>>(() => LoadAsync(k, typeId, kind, size))).Value;
    }

    private async Task<Bitmap?> LoadAsync(string key, int typeId, TypeImageKind kind, int size)
    {
        try
        {
            var file = Path.Combine(_cacheDirectory, key + ".png");
            if (File.Exists(file))
                return await Task.Run(() => new Bitmap(file));   // decode the cached file off the UI thread

            var asset = kind == TypeImageKind.Render ? "render" : "icon";
            var client = httpClientFactory.CreateClient(HttpClientName);
            var bytes = await client.GetByteArrayAsync($"types/{typeId}/{asset}?size={size}");

            Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllBytesAsync(file, bytes);

            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                return new Bitmap(stream);
            });
        }
        catch
        {
            // Don't keep a failed load cached, so the image is retried on the next request (offline -> online recovery).
            _cache.TryRemove(key, out _);
            return null;
        }
    }
}
