using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class DeleteFleetCommandHandler(IFleetRepository repository, IEventBus eventBus)
    : ICommandHandler<DeleteFleetCommand, Result>
{
    public async Task<Result> Handle(DeleteFleetCommand command, CancellationToken cancellationToken = default)
    {
        var fleet = await repository.GetAsync(command.FleetId, cancellationToken);
        if (fleet is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        // Measured before the write: once the fleet is gone there is no listing and no roster left to ask.
        var wasListed = await repository.IsOpenAsync(fleet.Id, cancellationToken);
        var members = await repository.ListMembersAsync(fleet.Id, cancellationToken);
        await repository.DeleteAsync(fleet.Id, cancellationToken);

        // Disbanded rather than a kind of its own: to everyone who watched it, a purged fleet is a disbanded one.
        await eventBus.PublishAsync(
            new FleetChangedEvent(new FleetChangePayload(fleet.Id, FleetChangeKind.Disbanded))
            {
                WasListed = wasListed,
                FormerRosterCharacterIds = [.. members.Select(member => member.CharacterId), fleet.CreatorCharacterId]
            },
            EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
