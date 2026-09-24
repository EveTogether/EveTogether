namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// A cached ESI response. <see cref="ExpiresAt"/> null = forever-immutable (killmails).
/// <see cref="ETag"/> drives conditional <c>If-None-Match</c> revalidation (weak ETags are not stored).
/// </summary>
/// <param name="Pages">ESI's <c>X-Pages</c>, replayed on a hit so it still paginates; null (older entries) means one page.</param>
public sealed record EsiCacheEntry(string Body, string? ETag, DateTimeOffset? ExpiresAt, DateTimeOffset StoredAt, int? Pages = null)
{
    public bool IsFresh(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}
