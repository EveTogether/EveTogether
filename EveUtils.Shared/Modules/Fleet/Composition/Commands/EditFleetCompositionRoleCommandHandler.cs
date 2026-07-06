using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

internal sealed class EditFleetCompositionRoleCommandHandler(
    IFleetCompositionRepository repository,
    FleetCompositionAuthorizer authorizer) : ICommandHandler<EditFleetCompositionRoleCommand, Result>
{
    public async Task<Result> Handle(EditFleetCompositionRoleCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.RoleName))
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Role name is required.", "FleetComposition"));

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

        role.RoleName = command.RoleName.Trim();
        role.GroupMinCount = command.GroupMinCount;

        await repository.UpdateRoleAsync(role, cancellationToken);
        return Result.Success();
    }
}
