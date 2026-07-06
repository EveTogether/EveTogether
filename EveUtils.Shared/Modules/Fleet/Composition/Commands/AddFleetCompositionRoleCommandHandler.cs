using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

internal sealed class AddFleetCompositionRoleCommandHandler(
    IFleetCompositionRepository repository,
    FleetCompositionAuthorizer authorizer) : ICommandHandler<AddFleetCompositionRoleCommand, Result<long>>
{
    public async Task<Result<long>> Handle(AddFleetCompositionRoleCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.RoleName))
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Role name is required.", "FleetComposition"));

        var composition = await repository.GetAsync(command.CompositionId, cancellationToken);
        if (composition is null)
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Composition not found.", "FleetComposition"));

        if (!await authorizer.CanManageAsync(composition, command.ActingCharacterId, cancellationToken))
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "You may not manage this composition.", "FleetComposition"));

        var existing = await repository.ListRolesAsync(command.CompositionId, cancellationToken);
        var id = await repository.AddRoleAsync(new FleetCompositionRole
        {
            CompositionId = command.CompositionId,
            RoleName = command.RoleName.Trim(),
            GroupMinCount = command.GroupMinCount,
            SortOrder = existing.Count
        }, cancellationToken);

        return Result<long>.Success(id);
    }
}
