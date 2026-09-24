using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class SetFleetEsiAutomationCommandHandler(IFleetRepository repository, IEventBus eventBus)
    : ICommandHandler<SetFleetEsiAutomationCommand, Result>
{
    public async Task<Result> Handle(SetFleetEsiAutomationCommand command, CancellationToken cancellationToken = default)
    {
        var fleet = await repository.GetAsync(command.FleetId, cancellationToken);
        if (fleet is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        // Owner-only: the toggles drive ESI writes against the owner's coupled fleet, so only its creator sets them.
        if (fleet.CreatorCharacterId != command.ActingCharacterId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed,
                "Only the fleet owner can change the ESI automation settings.", "Fleet"));

        fleet.EsiAutoApplyStructure = command.AutoApplyStructure;
        fleet.EsiAutoInviteMembers = command.AutoInviteMembers;
        await repository.UpdateAsync(fleet, cancellationToken);
        await eventBus.PublishAsync(
            new FleetChangedEvent(new FleetChangePayload(fleet.Id, FleetChangeKind.RosterChanged)) { ActingCharacterId = command.ActingCharacterId },
            EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
