namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// A cached ESI response. <see cref="ExpiresAt"/> null = forever-immutable (killmails).
/// <see cref="ETag"/> drives conditional <c>If-None-Match</c> revalidation (weak ETags are not stored).
/// </summary>
public sealed record EsiCacheEntry(string Body, string? ETag, DateTimeOffset? ExpiresAt, DateTimeOffset StoredAt)
{
    public bool IsFresh(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;
}
