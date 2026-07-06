namespace EveUtils.Shared.Modules.Esi;

/// <summary>EVE SSO v2 endpoints + JWT-validation constants. See ESI-Reference.md §6.</summary>
public static class EsiEndpoints
{
    public const string Authorize = "https://login.eveonline.com/v2/oauth/authorize";
    public const string Token = "https://login.eveonline.com/v2/oauth/token";
    public const string Metadata = "https://login.eveonline.com/.well-known/oauth-authorization-server";

    // Compatibility-date model: the base URL is version-less and the pinned
    // X-Compatibility-Date header below selects the API shape. Pinning (not /latest) keeps the code on a
    // known shape that never shifts under us. Bump CompatibilityDate deliberately when adopting a newer
    // ESI shape — see ESI-Reference.md §2 "bump process".
    public const string PublicDataBaseUrl = "https://esi.evetech.net";

    /// <summary>Pinned ESI compatibility date sent on every data call. Bump deliberately (ESI-Reference §2).</summary>
    // Bumped 2025-11-06 → 2026-06-09 (latest available, Cradle of War) to stay current. Verified safe by a full
    // OpenAPI-spec diff across every endpoint we call: the only schema-breaking change is GET /characters/{id}
    // (`title` → `corporation_title`, plus new `character_title_id` / `achievement_score`), but we read only
    // `name` / `corporation_id` / `alliance_id` — all unchanged — so nothing breaks and System.Text.Json ignores
    // the new fields. Surfacing the new title/achievement data (title name needs an SDE titles table) is separate.
    public const string CompatibilityDate = "2026-06-09";

    public const string Issuer = "login.eveonline.com";
    public const string IssuerHttps = "https://login.eveonline.com";
    public const string ExpectedAudience = "EVE Online";
    public const string SigningAlgorithm = "RS256";
}
