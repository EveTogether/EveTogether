using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Settings.Repositories;

namespace EveUtils.Client.Imaging;

/// <summary>
/// <see cref="ICharacterPortraitProvider"/> backed by the CCP image server, gated behind the opt-in image setting
/// and memoised in process + on disk (per-instance cache) so each portrait is fetched at most once and
/// renders offline afterwards. Any failure (offline, disabled, 404) yields null so the hex falls back to its glyph.
/// Reuses the shared <c>evetech-images</c> HttpClient registered for <see cref="TypeImageProvider"/>.
/// </summary>
public sealed class CharacterPortraitProvider(IHttpClientFactory httpClientFactory, ISettingRepository settings, string dataDirectory)
    : ICharacterPortraitProvider, ICharacterDataEraser
{
    public CharacterDataKind Kind => CharacterDataKind.Cache;

    private readonly string _cacheDirectory = Path.Combine(dataDirectory, "character-portraits");
    private readonly ConcurrentDictionary<string, Bitmap> _cache = new();

    private async Task<bool> AreImagesEnabledAsync(CancellationToken cancellationToken)
    {
        foreach (var setting in await settings.ListAsync(cancellationToken))
            if (setting.Key == TypeImageProvider.EnabledSettingKey)
                return !string.Equals(setting.Value, "false", StringComparison.OrdinalIgnoreCase);
        return true; // default on
    }

    public Task<Bitmap?> GetPortraitAsync(int characterId, int size, CancellationToken cancellationToken = default) =>
        _GetImageAsync(characterId, size, "characters", "portrait", cancellationToken);

    public Task<Bitmap?> GetCorporationLogoAsync(int corporationId, int size, CancellationToken cancellationToken = default) =>
        _GetImageAsync(corporationId, size, "corporations", "logo", cancellationToken);

    private async Task<Bitmap?> _GetImageAsync(int id, int size, string category, string asset, CancellationToken cancellationToken)
    {
        if (id <= 0) return null;

        var key = category == "characters" ? $"{id}_{size}" : $"corporation_{id}_{size}";
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
            var bytes = await client.GetByteArrayAsync($"{category}/{id}/{asset}?size={size}", cancellationToken);

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

    public Task EraseAsync(int characterId, string characterName, CancellationToken cancellationToken = default)
    {
        var prefix = $"{characterId}_";
        foreach (var key in _cache.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
            _cache.TryRemove(key, out _);

        if (!Directory.Exists(_cacheDirectory))
            return Task.CompletedTask;

        // Best effort, like the token store: a render that stays behind is a public image, fetched again on demand.
        foreach (var file in Directory.EnumerateFiles(_cacheDirectory, prefix + "*.png"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return Task.CompletedTask;
    }
}
