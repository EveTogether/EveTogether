using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class RenameSquadCommandHandler(IFleetRepository repository)
    : ICommandHandler<RenameSquadCommand, Result>
{
    public async Task<Result> Handle(RenameSquadCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Squad name is required.", "Fleet"));

        var squad = await repository.GetSquadAsync(command.SquadId, cancellationToken);
        if (squad is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Squad not found.", "Fleet"));

        var wing = await repository.GetWingAsync(squad.WingId, cancellationToken);
        if (wing is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Wing not found.", "Fleet"));

        var owned = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, wing.FleetId, command.ActingCharacterId, cancellationToken);
        if (!owned.IsSuccess)
            return Result.Failure(owned.Messages.ToArray());

        squad.Name = command.Name.Trim();
        await repository.UpdateSquadAsync(squad, cancellationToken);
        return Result.Success();
    }
}
