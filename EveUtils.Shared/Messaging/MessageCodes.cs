namespace EveUtils.Shared.Messaging;

/// <summary>Stable, machine-readable message codes. Reuse, do not renumber.</summary>
public static class MessageCodes
{
    public const string ScopeMissing = "SCOPE_MISSING";
    public const string ScopeForbidden = "SCOPE_FORBIDDEN";
    public const string AuthRequired = "AUTH_REQUIRED";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string RateLimited = "RATE_LIMITED";
    public const string SdeOutdated = "SDE_OUTDATED";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string BadRequest = "BAD_REQUEST";
    public const string NotFound = "NOT_FOUND";
    public const string EsiFailed = "ESI_FAILED";
    public const string ServerError = "SERVER_ERROR";
    public const string Timeout = "TIMEOUT";
    public const string ParseError = "PARSE_ERROR";
    public const string Duplicate = "DUPLICATE";
}
