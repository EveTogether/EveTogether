using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class RenameWingCommandHandler(IFleetRepository repository)
    : ICommandHandler<RenameWingCommand, Result>
{
    public async Task<Result> Handle(RenameWingCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Wing name is required.", "Fleet"));

        var wing = await repository.GetWingAsync(command.WingId, cancellationToken);
        if (wing is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Wing not found.", "Fleet"));

        var owned = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, wing.FleetId, command.ActingCharacterId, cancellationToken);
        if (!owned.IsSuccess)
            return Result.Failure(owned.Messages.ToArray());

        wing.Name = command.Name.Trim();
        await repository.UpdateWingAsync(wing, cancellationToken);
        return Result.Success();
    }
}
