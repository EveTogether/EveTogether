using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class DeleteSquadCommandHandler(IFleetRepository repository)
    : ICommandHandler<DeleteSquadCommand, Result>
{
    public async Task<Result> Handle(DeleteSquadCommand command, CancellationToken cancellationToken = default)
    {
        var squad = await repository.GetSquadAsync(command.SquadId, cancellationToken);
        if (squad is null)
            return Result.Success(); // already gone — idempotent

        var wing = await repository.GetWingAsync(squad.WingId, cancellationToken);
        if (wing is null)
            return Result.Success(); // orphaned/removed wing took the squad with it

        var owned = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, wing.FleetId, command.ActingCharacterId, cancellationToken);
        if (!owned.IsSuccess)
            return Result.Failure(owned.Messages.ToArray());

        await repository.DeleteSquadAsync(command.SquadId, cancellationToken);
        return Result.Success();
    }
}
