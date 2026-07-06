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
/// <see cref="ICharacterPortraitProvider"/> backed by the CCP image server, gated behind the opt-in image setting
/// and memoised in process + on disk (per-instance cache) so each portrait is fetched at most once and
/// renders offline afterwards. Any failure (offline, disabled, 404) yields null so the hex falls back to its glyph.
/// Reuses the shared <c>evetech-images</c> HttpClient registered for <see cref="TypeImageProvider"/>.
/// </summary>
public sealed class CharacterPortraitProvider(IHttpClientFactory httpClientFactory, ISettingRepository settings, string dataDirectory)
    : ICharacterPortraitProvider
{
    private readonly string _cacheDirectory = Path.Combine(dataDirectory, "character-portraits");
    private readonly ConcurrentDictionary<string, Bitmap> _cache = new();

    private async Task<bool> AreImagesEnabledAsync(CancellationToken cancellationToken)
    {
        foreach (var setting in await settings.ListAsync(cancellationToken))
            if (setting.Key == TypeImageProvider.EnabledSettingKey)
                return !string.Equals(setting.Value, "false", StringComparison.OrdinalIgnoreCase);
        return true; // default on
    }

    public async Task<Bitmap?> GetPortraitAsync(int characterId, int size, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0) return null;

        var key = $"{characterId}_{size}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        if (!await AreImagesEnabledAsync(cancellationToken))
            return null;

        try
        {
            var file = Path.Combine(_cacheDirectory, key + ".png");
            if (File.Exists(file))
                return _cache.GetOrAdd(key, _ => new Bitmap(file));

            var client = httpClientFactory.CreateClient(TypeImageProvider.HttpClientName);
            var bytes = await client.GetByteArrayAsync($"characters/{characterId}/portrait?size={size}", cancellationToken);

            Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllBytesAsync(file, bytes, cancellationToken);

            using var stream = new MemoryStream(bytes);
            // Decode eagerly and add the value (not a factory closure over the using-scoped stream): the stream is
            // disposed when this method returns, so a deferred factory could read a disposed stream.
            return _cache.GetOrAdd(key, new Bitmap(stream));
        }
        catch
        {
            return null;
        }
    }
}
