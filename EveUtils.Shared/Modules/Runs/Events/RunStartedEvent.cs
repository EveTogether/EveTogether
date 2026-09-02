using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Events;

public sealed class RunStartedEvent(
    Guid runId,
    long characterId,
    ActivityKind activityKind,
    DateTime startedAtUtc,
    long? fleetId,
    string? groupCode,
    bool isFleetCommander,
    string? solarSystemName = null,
    string? siteName = null)
    : IntegrationEvent<RunStartedEventData>(new RunStartedEventData(
        runId, characterId, activityKind, startedAtUtc, fleetId, groupCode, isFleetCommander, solarSystemName, siteName))
{
    public override string EventType => "runs.started";
}
