using System.Collections.Concurrent;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Server.Esi;

/// <summary>
/// A paired character's corporation, for grouping the Characters list. The server keeps no affiliation of its own, so
/// this asks public ESI through the metered affiliation resolver the pairing flow uses, and remembers the answer: a
/// character changes corporation rarely, and without this every page view would walk the same calls again. A failed
/// lookup is remembered briefly too, and the character is shown under an unknown corporation.
/// </summary>
public sealed class EsiCorporationLookup(IEsiAffiliationResolver resolver, TimeProvider clock) : ISingletonService
{
    private static readonly TimeSpan HitLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan MissLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<int, Entry> _cache = new();

    private readonly record struct Entry(string? Name, DateTimeOffset ExpiresAt);

    /// <summary>Corporation names per character id; characters ESI could not answer for are absent.</summary>
    public async Task<IReadOnlyDictionary<int, string>> ResolveAsync(IEnumerable<int> characterIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        foreach (var id in characterIds.Where(id => id > 0).Distinct())
        {
            if (!_cache.TryGetValue(id, out var entry) || entry.ExpiresAt <= clock.GetUtcNow())
            {
                var affiliation = await resolver.ResolveAsync(id, ct);
                var name = affiliation?.CorporationName;
                entry = new Entry(name, clock.GetUtcNow() + (name is null ? MissLifetime : HitLifetime));
                _cache[id] = entry;
            }
            if (entry.Name is { } corporation)
                result[id] = corporation;
        }
        return result;
    }
}
