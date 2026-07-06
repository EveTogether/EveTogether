namespace EveUtils.Shared.Messaging;

/// <summary>Result with a payload. <see cref="Value"/> is only valid when <see cref="Result.IsSuccess"/>.</summary>
public sealed class Result<T> : Result
{
    public T? Value { get; }

    private Result(bool isSuccess, T? value, IReadOnlyList<ResultMessage> messages)
        : base(isSuccess, messages)
    {
        Value = value;
    }

    public static Result<T> Success(T value, params ResultMessage[] messages) => new(true, value, messages);
    public static new Result<T> Failure(params ResultMessage[] messages) => new(false, default, messages);
}
