namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Internal marker headers the cache handler (Deel 3) adds to a synthetic response so the pivot can tell
/// a cache/304 hit from a fresh network read. Never sent to ESI; stripped of meaning outside the chain.
/// </summary>
public static class EsiCacheHeaders
{
    /// <summary>Present on a response the cache handler served from the local store (fresh hit or 304).</summary>
    public const string FromCache = "X-EveUtils-From-Cache";
}
