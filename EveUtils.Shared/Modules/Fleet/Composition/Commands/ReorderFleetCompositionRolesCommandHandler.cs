using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

internal sealed class ReorderFleetCompositionRolesCommandHandler(
    IFleetCompositionRepository repository,
    FleetCompositionAuthorizer authorizer) : ICommandHandler<ReorderFleetCompositionRolesCommand, Result>
{
    public async Task<Result> Handle(ReorderFleetCompositionRolesCommand command, CancellationToken cancellationToken = default)
    {
        var composition = await repository.GetAsync(command.CompositionId, cancellationToken);
        if (composition is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Composition not found.", "FleetComposition"));

        if (!await authorizer.CanManageAsync(composition, command.ActingCharacterId, cancellationToken))
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "You may not manage this composition.", "FleetComposition"));

        await repository.ReorderRolesAsync(command.CompositionId, command.OrderedRoleIds, cancellationToken);
        return Result.Success();
    }
}
