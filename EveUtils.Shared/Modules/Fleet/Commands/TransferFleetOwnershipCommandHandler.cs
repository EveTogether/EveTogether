using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class TransferFleetOwnershipCommandHandler(IFleetRepository repository)
    : ICommandHandler<TransferFleetOwnershipCommand, Result>
{
    public async Task<Result> Handle(TransferFleetOwnershipCommand command, CancellationToken cancellationToken = default)
    {
        // Creator-only on an existing, still-active fleet (NOT_FOUND / archived / non-creator handled here).
        var owned = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, command.FleetId, command.ActingCharacterId, cancellationToken);
        if (!owned.IsSuccess)
            return Result.Failure(owned.Messages.ToArray());

        var fleet = owned.Value!;

        if (command.NewOwnerCharacterId == fleet.CreatorCharacterId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "That character already owns the fleet.", "Fleet"));

        // The new owner must already be on the roster — ownership follows membership.
        if (!await repository.IsMemberAsync(fleet.Id, command.NewOwnerCharacterId, cancellationToken))
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "The new owner must be a fleet member.", "Fleet"));

        // The old owner stays on as a plain member; only the creator pointer moves.
        fleet.CreatorCharacterId = command.NewOwnerCharacterId;
        fleet.LastActivityAt = DateTimeOffset.UtcNow;
        await repository.UpdateAsync(fleet, cancellationToken);

        return Result.Success();
    }
}
