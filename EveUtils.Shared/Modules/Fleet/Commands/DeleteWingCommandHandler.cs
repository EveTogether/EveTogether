using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class DeleteWingCommandHandler(IFleetRepository repository)
    : ICommandHandler<DeleteWingCommand, Result>
{
    public async Task<Result> Handle(DeleteWingCommand command, CancellationToken cancellationToken = default)
    {
        var wing = await repository.GetWingAsync(command.WingId, cancellationToken);
        if (wing is null)
            return Result.Success(); // already gone — idempotent

        var owned = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, wing.FleetId, command.ActingCharacterId, cancellationToken);
        if (!owned.IsSuccess)
            return Result.Failure(owned.Messages.ToArray());

        await repository.DeleteWingAsync(command.WingId, cancellationToken);
        return Result.Success();
    }
}
