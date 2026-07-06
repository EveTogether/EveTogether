using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

internal sealed class EditFleetCompositionEntryCommandHandler(
    IFleetCompositionRepository repository,
    FleetCompositionAuthorizer authorizer) : ICommandHandler<EditFleetCompositionEntryCommand, Result>
{
    public async Task<Result> Handle(EditFleetCompositionEntryCommand command, CancellationToken cancellationToken = default)
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

        entry.EntryMinCount = command.EntryMinCount;

        await repository.UpdateEntryAsync(entry, cancellationToken);
        return Result.Success();
    }
}
