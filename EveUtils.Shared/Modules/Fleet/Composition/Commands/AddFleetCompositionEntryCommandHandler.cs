using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

internal sealed class AddFleetCompositionEntryCommandHandler(
    IFleetCompositionRepository repository,
    FleetCompositionAuthorizer authorizer) : ICommandHandler<AddFleetCompositionEntryCommand, Result<long>>
{
    public async Task<Result<long>> Handle(AddFleetCompositionEntryCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Fit is null || string.IsNullOrWhiteSpace(command.Fit.FitName) || command.Fit.ShipTypeId <= 0)
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "A fit with a ship and name is required.", "FleetComposition"));

        var role = await repository.GetRoleAsync(command.RoleId, cancellationToken);
        if (role is null)
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Role not found.", "FleetComposition"));

        var composition = await repository.GetAsync(role.CompositionId, cancellationToken);
        if (composition is null)
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Composition not found.", "FleetComposition"));

        if (!await authorizer.CanManageAsync(composition, command.ActingCharacterId, cancellationToken))
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "You may not manage this composition.", "FleetComposition"));

        var existing = await repository.ListEntriesAsync(command.RoleId, cancellationToken);
        var id = await repository.AddEntryAsync(new FleetCompositionEntry
        {
            RoleId = command.RoleId,
            Fit = command.Fit,
            EntryMinCount = command.EntryMinCount,
            SortOrder = existing.Count
        }, cancellationToken);

        return Result<long>.Success(id);
    }
}
