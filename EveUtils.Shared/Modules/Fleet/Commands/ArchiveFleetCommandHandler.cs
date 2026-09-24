using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class ArchiveFleetCommandHandler(IFleetRepository repository, IEventBus eventBus)
    : ICommandHandler<ArchiveFleetCommand, Result>
{
    public async Task<Result> Handle(ArchiveFleetCommand command, CancellationToken cancellationToken = default)
    {
        var fleet = await repository.GetAsync(command.FleetId, cancellationToken);
        if (fleet is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        if (fleet.State == FleetState.Archived)
            return Result.Success();

        // Measured before the write: archiving takes the fleet out of discovery.
        var wasListed = await repository.IsOpenAsync(fleet.Id, cancellationToken);
        fleet.State = FleetState.Archived;
        fleet.LastActivityAt = command.ArchivedAt;
        await repository.UpdateAsync(fleet, cancellationToken);
        await eventBus.PublishAsync(
            new FleetChangedEvent(new FleetChangePayload(fleet.Id, FleetChangeKind.Disbanded)) { WasListed = wasListed },
            EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
