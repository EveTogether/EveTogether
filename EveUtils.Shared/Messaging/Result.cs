namespace EveUtils.Shared.Messaging;

/// <summary>
/// Uniform result/envelope type: every call carries a status + structured
/// messages. No "silent" failures or separate error channels.
/// </summary>
public class Result
{
    public bool IsSuccess { get; }
    public IReadOnlyList<ResultMessage> Messages { get; }

    protected Result(bool isSuccess, IReadOnlyList<ResultMessage> messages)
    {
        IsSuccess = isSuccess;
        Messages = messages;
    }

    public static Result Success(params ResultMessage[] messages) => new(true, messages);
    public static Result Failure(params ResultMessage[] messages) => new(false, messages);
}
