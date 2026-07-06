using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// A structured ESI failure (ESI-Reference §9). <see cref="Kind"/> is the programmatic category,
/// <see cref="Code"/> the stable envelope string, and <see cref="HttpStatus"/> is surfaced so
/// callers can act on the raw status when they need to.
/// </summary>
public sealed record EsiError(
    EsiErrorKind Kind,
    string Code,
    string Message,
    int? HttpStatus = null,
    TimeSpan? RetryAfter = null,
    EsiRateLimitKind? RateLimitKind = null)
{
    /// <summary>Builds an error, deriving the stable <see cref="Code"/> from <paramref name="kind"/>.</summary>
    public static EsiError Of(
        EsiErrorKind kind,
        string message,
        int? httpStatus = null,
        TimeSpan? retryAfter = null,
        EsiRateLimitKind? rateLimitKind = null) =>
        new(kind, CodeFor(kind), message, httpStatus, retryAfter, rateLimitKind);

    /// <summary>Bridges to the envelope so callers/UI render one message channel.</summary>
    public ResultMessage ToResultMessage(string? source = null) =>
        new(MessageSeverity.Error, Code, Message, source);

    private static string CodeFor(EsiErrorKind kind) => kind switch
    {
        EsiErrorKind.BadRequest => MessageCodes.BadRequest,
        EsiErrorKind.AuthRequired => MessageCodes.AuthRequired,
        EsiErrorKind.ScopeMissing => MessageCodes.ScopeMissing,
        EsiErrorKind.ScopeForbidden => MessageCodes.ScopeForbidden,
        EsiErrorKind.NotFound => MessageCodes.NotFound,
        EsiErrorKind.RateLimited => MessageCodes.RateLimited,
        EsiErrorKind.ServerError => MessageCodes.ServerError,
        EsiErrorKind.Timeout => MessageCodes.Timeout,
        EsiErrorKind.Network => MessageCodes.EsiFailed,
        EsiErrorKind.ParseError => MessageCodes.ParseError,
        _ => MessageCodes.EsiFailed
    };
}
