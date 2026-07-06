namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Typed <see cref="HttpRequestOptionsKey{T}"/> keys used to carry per-request hints down the handler
/// chain via <see cref="HttpRequestMessage.Options"/> (the idiomatic way, Deel 6).
/// </summary>
public static class EsiRequestOptions
{
    /// <summary>Per-call <c>X-Compatibility-Date</c> override; absent = the pinned default (Deel 6).</summary>
    public static readonly HttpRequestOptionsKey<string> CompatibilityDate = new("esi.compatibility-date");

    /// <summary>The bucket key (<c>app:characterId</c> / <c>ip</c>) the rate-limit handler accounts under (Deel 4).</summary>
    public static readonly HttpRequestOptionsKey<string> BucketKey = new("esi.bucket-key");

    /// <summary>The normalised route template (<c>/characters/{id}/</c>) the rate-limit handler accounts per-endpoint under.</summary>
    public static readonly HttpRequestOptionsKey<string> EndpointKey = new("esi.endpoint-key");

    /// <summary>Resolves the bucket key carried on the request; defaults to <c>ip</c> for a public call.</summary>
    public static string ResolveBucketKey(HttpRequestMessage request) =>
        request.Options.TryGetValue(BucketKey, out var key) && !string.IsNullOrEmpty(key) ? key : "ip";

    /// <summary>Resolves the endpoint route template carried on the request; defaults to <c>?</c> when absent.</summary>
    public static string ResolveEndpointKey(HttpRequestMessage request) =>
        request.Options.TryGetValue(EndpointKey, out var key) && !string.IsNullOrEmpty(key) ? key : "?";
}
