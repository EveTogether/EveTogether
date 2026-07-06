using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>ESI outcome carrying a deserialized payload. <see cref="Value"/> is only valid on success.</summary>
public sealed class EsiResult<T> : EsiResult
{
    public T? Value { get; }

    private EsiResult(bool isSuccess, T? value, bool fromCache, EsiError? error)
        : base(isSuccess, fromCache, error)
    {
        Value = value;
    }

    public static EsiResult<T> Ok(T value, bool fromCache = false) => new(true, value, fromCache, null);
    public static new EsiResult<T> Fail(EsiError error) => new(false, default, false, error);

    /// <summary>Projects to the typed envelope.</summary>
    public new Result<T> ToResult(string? source = null) =>
        IsSuccess ? Result<T>.Success(Value!) : Result<T>.Failure(Error!.ToResultMessage(source));
}
