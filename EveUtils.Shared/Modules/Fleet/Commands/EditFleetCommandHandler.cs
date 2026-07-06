using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class EditFleetCommandHandler(IFleetRepository repository)
    : ICommandHandler<EditFleetCommand, Result>
{
    public async Task<Result> Handle(EditFleetCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Fleet name is required.", "Fleet"));

        var fleet = await repository.GetAsync(command.FleetId, cancellationToken);
        if (fleet is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        // Per-fleet authorization: only the creator may edit it.
        if (fleet.CreatorCharacterId != command.ActingCharacterId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "Only the fleet's creator can edit it.", "Fleet"));

        fleet.Name = command.Name.Trim();
        fleet.Description = command.Description;
        fleet.Visibility = command.Visibility;
        fleet.FromTime = command.FromTime;
        fleet.ToTime = command.ToTime;
        fleet.OfflineBehavior = command.OfflineBehavior;
        fleet.LastActivityAt = DateTimeOffset.UtcNow;

        await repository.UpdateAsync(fleet, cancellationToken);
        return Result.Success();
    }
}
