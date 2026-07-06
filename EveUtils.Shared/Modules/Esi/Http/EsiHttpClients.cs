namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary><see cref="IHttpClientFactory"/> client names for the ESI layer.</summary>
public static class EsiHttpClients
{
    /// <summary>ESI data API — carries the full handler chain (header → cache → rate-limit → retry).</summary>
    public const string Data = "esi";

    /// <summary>EVE SSO token endpoint — a bare client (header handler only; no cache/rate-limit/retry).</summary>
    public const string Auth = "esi-auth";

    /// <summary>
    /// Legacy ESI clients (affiliation/public-info/fittings): header centralisation only. They keep their
    /// own behaviour (the fittings client has its own cache + retry); full migration to the pivot — which
    /// carries the proven chain — is a follow-up so their flows can be re-tested separately.
    /// </summary>
    public const string Legacy = "esi-legacy";
}
