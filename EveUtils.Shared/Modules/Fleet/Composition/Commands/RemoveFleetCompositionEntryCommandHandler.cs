using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

internal sealed class RemoveFleetCompositionEntryCommandHandler(
    IFleetCompositionRepository repository,
    FleetCompositionAuthorizer authorizer) : ICommandHandler<RemoveFleetCompositionEntryCommand, Result>
{
    public async Task<Result> Handle(RemoveFleetCompositionEntryCommand command, CancellationToken cancellationToken = default)
    {
        var entry = await repository.GetEntryAsync(command.EntryId, cancellationToken);
        if (entry is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Entry not found.", "FleetComposition"));

        var role = await repository.GetRoleAsync(entry.RoleId, cancellationToken);
        var composition = role is null ? null : await repository.GetAsync(role.CompositionId, cancellationToken);
        if (composition is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Composition not found.", "FleetComposition"));

        if (!await authorizer.CanManageAsync(composition, command.ActingCharacterId, cancellationToken))
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "You may not manage this composition.", "FleetComposition"));

        await repository.DeleteEntryAsync(command.EntryId, cancellationToken);
        return Result.Success();
    }
}
