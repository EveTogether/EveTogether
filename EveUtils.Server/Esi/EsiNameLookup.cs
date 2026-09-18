using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Server.Esi;

/// <summary>
/// Public ESI name lookup (<c>POST /universe/names</c>) for the panel's ids that no paired character explains —
/// a fleet creator or composition owner who never paired here. Cached in memory: the pivot's own file cache does
/// not help a POST, and without this every pageview and every 2s dashboard tick would go back to ESI.
/// Failures are cached too (briefly) so an unreachable ESI or an id it does not know costs one call per window,
/// not one per render; a failed lookup returns no name and the caller shows the id.
/// </summary>
public sealed class EsiNameLookup(IEsiClient esi, ILogger<EsiNameLookup> logger, TimeProvider clock) : ISingletonService
{
    private static readonly TimeSpan HitLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan MissLifetime = TimeSpan.FromMinutes(10);
    private const int BatchSize = 1000;

    private readonly ConcurrentDictionary<long, Entry> _cache = new();

    private readonly record struct Entry(string? Name, DateTimeOffset ExpiresAt);

    /// <summary>Names for the ids ESI knows; ids it does not know (or could not be asked about) are absent.</summary>
    public async Task<IReadOnlyDictionary<long, string>> ResolveAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var wanted = ids.Where(id => id > 0).Distinct().ToList();
        var stale = wanted.Where(id => !_cache.TryGetValue(id, out var e) || e.ExpiresAt <= now).ToList();

        foreach (var batch in stale.Chunk(BatchSize))
            await FetchAsync(batch, ct);

        var result = new Dictionary<long, string>();
        foreach (var id in wanted)
        {
            if (_cache.TryGetValue(id, out var entry) && entry.Name is not null)
                result[id] = entry.Name;
        }
        return result;
    }

    private async Task FetchAsync(long[] batch, CancellationToken ct)
    {
        var response = await esi.RequestAsync<List<NameEntry>>(
            EsiRequest.Post("/universe/names/", JsonSerializer.Serialize(batch)), ct);

        if (response is { IsSuccess: true, Value: not null })
        {
            var now = clock.GetUtcNow();
            // /universe/names names any entity kind; an id that is a solar system or corp is not a character here.
            foreach (var entry in response.Value.Where(e => e.Category == "character"))
                _cache[entry.Id] = new Entry(entry.Name, now + HitLifetime);
            Remember(batch.Where(id => !_cache.TryGetValue(id, out var e) || e.ExpiresAt <= now));
            return;
        }

        // One id ESI does not know fails the whole batch with a 404, so retry singly rather than lose every name.
        if (batch.Length > 1 && response.Error?.HttpStatus == 404)
        {
            foreach (var id in batch)
                await FetchAsync([id], ct);
            return;
        }

        logger.LogWarning("ESI name lookup for {Count} id(s) failed: {Error}", batch.Length, response.Error?.Message);
        Remember(batch);
    }

    private void Remember(IEnumerable<long> unresolved)
    {
        var expiresAt = clock.GetUtcNow() + MissLifetime;
        foreach (var id in unresolved)
            _cache[id] = new Entry(null, expiresAt);
    }

    internal sealed class NameEntry
    {
        [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }
}
