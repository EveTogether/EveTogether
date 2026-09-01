using EveUtils.Shared.Modules.Runs.Entities;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed class RunWirePayload
{
    public required Run Run { get; init; }
    public required long SentAtUnixMilliseconds { get; init; }
}
