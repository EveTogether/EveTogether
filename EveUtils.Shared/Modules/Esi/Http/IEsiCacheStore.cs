namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Persistent ESI response cache: an in-memory hot layer over a file store, keyed by a hash of the
/// request URL (the token is never part of the key — the character id is already in the path). Survives
/// restarts so we never re-fetch before <c>Expires</c> (ban-safe, §5).
/// </summary>
public interface IEsiCacheStore
{
    Task<EsiCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, EsiCacheEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Removes entries whose <see cref="EsiCacheEntry.ExpiresAt"/> has passed. Returns the count purged.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
