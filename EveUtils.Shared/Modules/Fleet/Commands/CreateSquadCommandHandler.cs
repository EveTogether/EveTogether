using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class CreateSquadCommandHandler(IFleetRepository repository)
    : ICommandHandler<CreateSquadCommand, Result<long>>
{
    public async Task<Result<long>> Handle(CreateSquadCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Squad name is required.", "Fleet"));

        var wing = await repository.GetWingAsync(command.WingId, cancellationToken);
        if (wing is null)
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Wing not found.", "Fleet"));

        var owned = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, wing.FleetId, command.ActingCharacterId, cancellationToken);
        if (!owned.IsSuccess)
            return Result<long>.Failure(owned.Messages.ToArray());

        var squads = await repository.ListSquadsAsync(command.WingId, cancellationToken);
        if (squads.Count >= FleetStructureLimits.MaxSquadsPerWing)
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed,
                $"A wing can have at most {FleetStructureLimits.MaxSquadsPerWing} squads.", "Fleet"));

        var id = await repository.AddSquadAsync(new FleetSquad
        {
            WingId = command.WingId,
            Name = command.Name.Trim()
        }, cancellationToken);

        return Result<long>.Success(id);
    }
}
