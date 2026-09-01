using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.Fleet;

public sealed class FleetRunGroupCodePublisher(IEventBus eventBus) : ITransientService
{
    public Task PublishAsync(int characterId, long fleetId, ActivityKind activityKind, string groupCode,
        DateTime startedAtUtc, CancellationToken cancellationToken = default) =>
        eventBus.PublishAsync(new FleetRunGroupCodeEvent(
            new RunGroupCodeStart(fleetId, activityKind, groupCode, startedAtUtc), characterId),
            EventTarget.Both, cancellationToken);
}
