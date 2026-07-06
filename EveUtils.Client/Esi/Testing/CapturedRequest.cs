using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.Esi.Testing;

/// <summary>Snapshot of the request headers/options the stub saw, so scenarios can assert on them.</summary>
public sealed record CapturedRequest(
    string Uri,
    string? UserAgent,
    string? CompatibilityDate,
    string Accept,
    string? Authorization,
    string? IfNoneMatch,
    string? BucketKey)
{
    public static CapturedRequest From(HttpRequestMessage request) => new(
        request.RequestUri?.AbsoluteUri ?? "",
        request.Headers.TryGetValues("User-Agent", out var ua) ? ua.FirstOrDefault() : null,
        request.Headers.TryGetValues("X-Compatibility-Date", out var compat) ? compat.FirstOrDefault() : null,
        request.Headers.Accept.ToString(),
        request.Headers.Authorization?.ToString(),
        request.Headers.IfNoneMatch.Count > 0 ? request.Headers.IfNoneMatch.ToString() : null,
        request.Options.TryGetValue(EsiRequestOptions.BucketKey, out var bucket) ? bucket : null);
}
