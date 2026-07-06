using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class CoupleFleetToEsiCommandHandler(IFleetRepository repository)
    : ICommandHandler<CoupleFleetToEsiCommand, Result>
{
    public async Task<Result> Handle(CoupleFleetToEsiCommand command, CancellationToken cancellationToken = default)
    {
        if (command.EsiFleetId <= 0)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "A valid in-game fleet id is required.", "Fleet"));

        var fleet = await repository.GetAsync(command.FleetId, cancellationToken);
        if (fleet is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        // Owner-only: coupling binds the plan to a real fleet, so only the fleet's creator may do it.
        if (fleet.CreatorCharacterId != command.ActingCharacterId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed,
                "Only the fleet owner can couple it to an in-game fleet.", "Fleet"));

        // Q4: one internal fleet ↔ one in-game fleet. Re-coupling overwrites the previous link (the owner re-formed
        // the in-game fleet); the reverse uniqueness (two internal fleets → one esiFleetId) is left to a later guard.
        fleet.EsiFleetId = command.EsiFleetId;
        fleet.EsiFleetBossId = command.EsiFleetBossId;
        fleet.EsiSyncState = EsiFleetSyncState.Linked;
        await repository.UpdateAsync(fleet, cancellationToken);
        return Result.Success();
    }
}
