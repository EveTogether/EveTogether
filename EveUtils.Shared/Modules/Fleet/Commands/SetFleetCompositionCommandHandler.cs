using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class SetFleetCompositionCommandHandler(IFleetRepository repository, IEventBus eventBus)
    : ICommandHandler<SetFleetCompositionCommand, Result>
{
    public async Task<Result> Handle(SetFleetCompositionCommand command, CancellationToken cancellationToken = default)
    {
        var fleet = await repository.GetAsync(command.FleetId, cancellationToken);
        if (fleet is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        if (fleet.CreatorCharacterId != command.ActingCharacterId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "Only the fleet's creator can couple a composition.", "Fleet"));

        if (fleet.Activation != FleetActivation.Forming)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "A composition can only be coupled while the fleet is forming.", "Fleet"));

        fleet.FleetCompositionId = command.CompositionId;
        fleet.LastActivityAt = DateTimeOffset.UtcNow;
        await repository.UpdateAsync(fleet, cancellationToken);
        await eventBus.PublishAsync(
            new FleetChangedEvent(new FleetChangePayload(fleet.Id, FleetChangeKind.CompositionChanged)) { ActingCharacterId = command.ActingCharacterId },
            EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
