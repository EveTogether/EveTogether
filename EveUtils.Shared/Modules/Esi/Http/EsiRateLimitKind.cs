namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Which of ESI's two independent rate-limit systems produced a <c>RATE_LIMITED</c> result
/// (ESI-Reference §4). The two behave differently, so a single code carries the distinction
/// via this kind rather than splitting into two codes.
/// </summary>
public enum EsiRateLimitKind
{
    /// <summary>The legacy per-IP error limit (HTTP 420; <c>X-ESI-Error-Limit-*</c> headers, §4a).</summary>
    ErrorLimit,

    /// <summary>The sliding-window token bucket (HTTP 429; <c>X-Ratelimit-*</c> + <c>Retry-After</c>, §4b).</summary>
    Bucket
}
