using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Client.Esi;

/// <summary>
/// Caches a character's public affiliation for the UI, resolving it through the shared metered
/// <see cref="IEsiAffiliationResolver"/> (the <c>/characters</c> → <c>/corporations</c> + <c>/alliances</c>
/// pivot, all public, no token). The resolver goes through <c>IEsiClient</c> so the calls are
/// rate-limited, file-cached at ESI's own TTLs and visible in the ESI metrics. Resolved info is held in memory;
/// the file cache backs the cheap re-fetches that <see cref="RefreshAsync"/> drives.
/// </summary>
public sealed class CharacterInfoService(IEsiAffiliationResolver resolver) : ICharacterInfoService
{
    private readonly ConcurrentDictionary<int, CharacterPublicInfo> _cache = new();
    private readonly ConcurrentDictionary<int, string?> _nameCache = new();

    public event Action<int, CharacterPublicInfo?>? AffiliationChanged;

    public CharacterPublicInfo? GetCached(int characterId) =>
        _cache.TryGetValue(characterId, out var info) ? info : null;

    public async Task<string?> GetNameAsync(int characterId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
            return null;
        if (_nameCache.TryGetValue(characterId, out var cached))
            return cached;

        var resolved = await resolver.ResolveAsync(characterId, cancellationToken);
        var name = resolved?.CharacterName;
        _nameCache[characterId] = name; // cache the miss too, mirroring the legacy per-session name cache
        return name;
    }

    public async Task<CharacterPublicInfo?> RefreshAsync(int characterId, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
            return null;

        var resolved = await resolver.ResolveAsync(characterId, cancellationToken);
        if (resolved is not null)
            _nameCache[characterId] = resolved.CharacterName;

        var info = _ToAffiliation(resolved);
        if (info is null)
            return GetCached(characterId); // transient ESI failure / no affiliation → keep the last good value

        var changed = !_cache.TryGetValue(characterId, out var previous) || previous != info;
        _cache[characterId] = info;
        if (changed)
            AffiliationChanged?.Invoke(characterId, info);

        return info;
    }

    /// <summary>
    /// Maps a resolved identity to the UI affiliation, or null when there is nothing to show — preserving the
    /// "keep last good, don't blank" contract: an unresolved character (resolver null) and a character with
    /// neither corp nor alliance both yield null so the cache is not overwritten with a worse value.
    /// </summary>
    private static CharacterPublicInfo? _ToAffiliation(EsiCharacterAffiliation? resolved)
    {
        if (resolved is null || (resolved.CorporationName is null && resolved.AllianceName is null))
            return null;

        return new CharacterPublicInfo(
            resolved.CorporationName, resolved.CorporationTicker,
            resolved.AllianceName, resolved.AllianceTicker);
    }
}
