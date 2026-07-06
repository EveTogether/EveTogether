using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Uniform outcome of an ESI call. The whole ESI layer funnels success and failure through
/// this one shape so callers never see raw HTTP details — only <see cref="IsSuccess"/> plus a
/// structured <see cref="Error"/>. Payload-carrying calls use <see cref="EsiResult{T}"/>.
/// </summary>
public class EsiResult
{
    public bool IsSuccess { get; }

    /// <summary>True when the value was served from the local cache (a 304/fresh hit), not the network.</summary>
    public bool FromCache { get; }

    public EsiError? Error { get; }

    protected EsiResult(bool isSuccess, bool fromCache, EsiError? error)
    {
        IsSuccess = isSuccess;
        FromCache = fromCache;
        Error = error;
    }

    public static EsiResult Ok(bool fromCache = false) => new(true, fromCache, null);
    public static EsiResult Fail(EsiError error) => new(false, false, error);

    /// <summary>Projects to the envelope (success carries no message; failure carries the error).</summary>
    public Result ToResult(string? source = null) =>
        IsSuccess ? Result.Success() : Result.Failure(Error!.ToResultMessage(source));
}
