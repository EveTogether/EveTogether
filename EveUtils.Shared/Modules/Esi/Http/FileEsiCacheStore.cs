using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// File-backed ESI cache with a <see cref="ConcurrentDictionary{TKey,TValue}"/> hot layer. Each entry
/// is one JSON file named after a hash of the request URL, written atomically (temp + rename) so a crash
/// never leaves a torn file. Persisting across restarts keeps us ban-safe — we never re-fetch before
/// <c>Expires</c> (§5).
/// </summary>
public sealed class FileEsiCacheStore : IEsiCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _directory;
    private readonly ConcurrentDictionary<string, EsiCacheEntry> _hot = new();

    public FileEsiCacheStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Hashes a request URL into a stable, filesystem-safe cache key (token excluded).</summary>
    public static string KeyFor(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));

    public async Task<EsiCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_hot.TryGetValue(key, out var hot))
            return hot;

        var path = PathFor(key);
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            var entry = await JsonSerializer.DeserializeAsync<EsiCacheEntry>(stream, JsonOptions, cancellationToken);
            if (entry is not null)
                _hot[key] = entry;
            return entry;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null; // a corrupt/torn entry is simply a cache miss
        }
    }

    public async Task SetAsync(string key, EsiCacheEntry entry, CancellationToken cancellationToken = default)
    {
        _hot[key] = entry;

        var path = PathFor(key);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, entry, JsonOptions, cancellationToken);
        }
        File.Move(temp, path, overwrite: true);
    }

    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var purged = 0;

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var entry = JsonSerializer.Deserialize<EsiCacheEntry>(File.ReadAllText(file), JsonOptions);
                if (entry is not null && entry.IsFresh(now))
                    continue;

                File.Delete(file);
                _hot.TryRemove(Path.GetFileNameWithoutExtension(file), out _);
                purged++;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                File.Delete(file); // unreadable → drop it
                purged++;
            }
        }

        return Task.FromResult(purged);
    }

    private string PathFor(string key) => Path.Combine(_directory, $"{key}.json");
}
