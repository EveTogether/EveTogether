using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

internal sealed class RemoveFleetCompositionRoleCommandHandler(
    IFleetCompositionRepository repository,
    FleetCompositionAuthorizer authorizer) : ICommandHandler<RemoveFleetCompositionRoleCommand, Result>
{
    public async Task<Result> Handle(RemoveFleetCompositionRoleCommand command, CancellationToken cancellationToken = default)
    {
        var role = await repository.GetRoleAsync(command.RoleId, cancellationToken);
        if (role is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Role not found.", "FleetComposition"));

        var composition = await repository.GetAsync(role.CompositionId, cancellationToken);
        if (composition is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Composition not found.", "FleetComposition"));

        if (!await authorizer.CanManageAsync(composition, command.ActingCharacterId, cancellationToken))
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "You may not manage this composition.", "FleetComposition"));

        await repository.DeleteRoleAsync(command.RoleId, cancellationToken);
        return Result.Success();
    }
}
