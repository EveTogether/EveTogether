using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

internal sealed class ReorderFleetCompositionEntriesCommandHandler(
    IFleetCompositionRepository repository,
    FleetCompositionAuthorizer authorizer) : ICommandHandler<ReorderFleetCompositionEntriesCommand, Result>
{
    public async Task<Result> Handle(ReorderFleetCompositionEntriesCommand command, CancellationToken cancellationToken = default)
    {
        var role = await repository.GetRoleAsync(command.RoleId, cancellationToken);
        var composition = role is null ? null : await repository.GetAsync(role.CompositionId, cancellationToken);
        if (composition is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Composition not found.", "FleetComposition"));

        if (!await authorizer.CanManageAsync(composition, command.ActingCharacterId, cancellationToken))
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "You may not manage this composition.", "FleetComposition"));

        await repository.ReorderEntriesAsync(command.RoleId, command.OrderedEntryIds, cancellationToken);
        return Result.Success();
    }
}
