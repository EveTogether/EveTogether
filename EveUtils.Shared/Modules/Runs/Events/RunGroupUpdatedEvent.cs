using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Events;

/// <summary>
/// Pushed by the server, after it accepted a run, to every other character holding a run in the same group (ET-245) —
/// exactly the characters its pull would hand that run to. Server-sourced: a group lives on one server, and the pull
/// it prompts has to go to the server that sent it, never to whichever one is coupled first.
/// </summary>
public sealed class RunGroupUpdatedEvent(RunGroupUpdate data, int? characterId = null)
    : IntegrationEvent<RunGroupUpdate>(data, characterId), IServerSourcedEvent
{
    public override string EventType => "runs.group-updated";

    public string? SourceServerAddress { get; set; }
}
