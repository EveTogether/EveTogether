namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Programmatic category of an ESI failure (ESI-Reference §9). Callers switch on this; the matching
/// stable string for the envelope lives in <see cref="EsiError.Code"/>.
/// </summary>
public enum EsiErrorKind
{
    /// <summary>400/422 or an SSO request-shape error — our call is wrong, do not retry.</summary>
    BadRequest,

    /// <summary>401, or refresh failed — the character must re-authenticate.</summary>
    AuthRequired,

    /// <summary>Pre-flight found the required scope was never granted — the call was not sent.</summary>
    ScopeMissing,

    /// <summary>403 despite the scope being granted — ESI refuses (corp role/standing/structure access).</summary>
    ScopeForbidden,

    /// <summary>404 — the resource does not exist; treat as empty/unknown.</summary>
    NotFound,

    /// <summary>420 or 429 — see <see cref="EsiError.RateLimitKind"/> for which system.</summary>
    RateLimited,

    /// <summary>5xx (except 504) — transient server fault.</summary>
    ServerError,

    /// <summary>The local gate withheld the call because ESI is down (failed /status/ poll or the 11:00 UTC window),
    /// so it never reached the network — expected, not an app error.</summary>
    Unavailable,

    /// <summary>504 or a client-side timeout/cancellation of the request.</summary>
    Timeout,

    /// <summary>A network/transport failure before any HTTP status was seen.</summary>
    Network,

    /// <summary>The body could not be deserialized into the expected shape (empty/HTML/garbage).</summary>
    ParseError
}
