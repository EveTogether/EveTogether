using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Ships.Dtos;
using EveUtils.Shared.Modules.Ships.Entities;
using EveUtils.Shared.Modules.Ships.Events;
using EveUtils.Shared.Modules.Ships.Repositories;

namespace EveUtils.Shared.Modules.Ships.Commands;

internal sealed class AddShipCommandHandler(IShipRepository repository, IEventBus eventBus)
    : ICommandHandler<AddShipCommand, Result<int>>
{
    public async Task<Result<int>> Handle(AddShipCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return Result<int>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Ship name is required.", "Ships"));
        }

        var id = await repository.AddAsync(
            new Ship { Name = command.Name, Class = command.Class, Mass = command.Mass },
            cancellationToken);

        // The module announces a new ship — published Both so fleet members (other connected clients)
        // see it appear too (foundation "samen + data delen"; the local UI also updates off this event).
        var dto = new ShipDto(id, command.Name, command.Class, command.Mass);
        await eventBus.PublishAsync(new ShipAddedEvent(dto), EventTarget.Both, cancellationToken);

        return Result<int>.Success(id);
    }
}
