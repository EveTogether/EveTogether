using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Composition.Commands;

internal sealed class EditFleetCompositionCommandHandler(
    IFleetCompositionRepository repository,
    FleetCompositionAuthorizer authorizer) : ICommandHandler<EditFleetCompositionCommand, Result>
{
    public async Task<Result> Handle(EditFleetCompositionCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Composition name is required.", "FleetComposition"));

        var composition = await repository.GetAsync(command.CompositionId, cancellationToken);
        if (composition is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Composition not found.", "FleetComposition"));

        if (!await authorizer.CanManageAsync(composition, command.ActingCharacterId, cancellationToken))
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "You may not manage this composition.", "FleetComposition"));

        composition.Name = command.Name.Trim();
        composition.Description = command.Description;
        composition.UpdatedAt = DateTimeOffset.UtcNow;

        await repository.UpdateAsync(composition, cancellationToken);
        return Result.Success();
    }
}
