using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class CreateWingCommandHandler(IFleetRepository repository)
    : ICommandHandler<CreateWingCommand, Result<long>>
{
    public async Task<Result<long>> Handle(CreateWingCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Wing name is required.", "Fleet"));

        var owned = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, command.FleetId, command.ActingCharacterId, cancellationToken);
        if (!owned.IsSuccess)
            return Result<long>.Failure(owned.Messages.ToArray());

        var wings = await repository.ListWingsAsync(command.FleetId, cancellationToken);
        if (wings.Count >= FleetStructureLimits.MaxWingsPerFleet)
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed,
                $"A fleet can have at most {FleetStructureLimits.MaxWingsPerFleet} wings.", "Fleet"));

        var id = await repository.AddWingAsync(new FleetWing
        {
            FleetId = command.FleetId,
            Name = command.Name.Trim()
        }, cancellationToken);

        return Result<long>.Success(id);
    }
}
