namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class RunWirePayload
{
    public required RunWireData Run { get; init; }
    public required long SentAtUnixMilliseconds { get; init; }
}
