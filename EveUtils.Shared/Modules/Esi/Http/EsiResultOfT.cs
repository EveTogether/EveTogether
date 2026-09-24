using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>ESI outcome carrying a deserialized payload. <see cref="Value"/> is only valid on success.</summary>
public sealed class EsiResult<T> : EsiResult
{
    public T? Value { get; }

    /// <summary>ESI's <c>X-Pages</c> on a paginated endpoint; 1 when the response carries none.</summary>
    public int Pages { get; }

    private EsiResult(bool isSuccess, T? value, bool fromCache, EsiError? error, int pages = 1)
        : base(isSuccess, fromCache, error)
    {
        Value = value;
        Pages = pages;
    }

    public static EsiResult<T> Ok(T value, bool fromCache = false, int pages = 1) => new(true, value, fromCache, null, pages);
    public static new EsiResult<T> Fail(EsiError error) => new(false, default, false, error);

    /// <summary>Projects to the typed envelope.</summary>
    public new Result<T> ToResult(string? source = null) =>
        IsSuccess ? Result<T>.Success(Value!) : Result<T>.Failure(Error!.ToResultMessage(source));
}
