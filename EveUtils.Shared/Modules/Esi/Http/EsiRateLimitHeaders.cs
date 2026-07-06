namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// The two independent ESI rate-limit header sets, parsed from a response (ESI-Reference §4/§9). Both are
/// header-driven only — the bucket limit is not derivable from the spec (§9c). Any field is null when its
/// header is absent (a 429 has no guaranteed headers; the error-limit and bucket headers are mutually
/// exclusive per route).
/// </summary>
public sealed record EsiRateLimitHeaders(
    int? ErrorRemaining,
    DateTimeOffset? ErrorResetAt,
    string? BucketGroup,
    int? BucketLimit,
    int? BucketRemaining,
    int? BucketUsed,
    TimeSpan? RetryAfter)
{
    public static EsiRateLimitHeaders Parse(HttpResponseMessage response)
    {
        var headers = response.Headers;

        int? errorRemaining = TryInt(headers, "X-ESI-Error-Limit-Remain");
        DateTimeOffset? errorResetAt = TryInt(headers, "X-ESI-Error-Limit-Reset") is { } reset
            ? DateTimeOffset.UtcNow.AddSeconds(reset)
            : null;

        var bucketGroup = TryString(headers, "X-Ratelimit-Group");
        // X-Ratelimit-Limit is "150/15m"; keep the leading request count.
        var bucketLimit = TryString(headers, "X-Ratelimit-Limit") is { } raw &&
                          int.TryParse(raw.Split('/', 2)[0], out var limit)
            ? limit
            : (int?)null;
        var bucketRemaining = TryInt(headers, "X-Ratelimit-Remaining");
        var bucketUsed = TryInt(headers, "X-Ratelimit-Used");

        return new EsiRateLimitHeaders(
            errorRemaining, errorResetAt, bucketGroup, bucketLimit, bucketRemaining, bucketUsed,
            headers.RetryAfter?.Delta);
    }

    private static int? TryInt(System.Net.Http.Headers.HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out var parsed)
            ? parsed
            : null;

    private static string? TryString(System.Net.Http.Headers.HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
