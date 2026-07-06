using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class UncoupleFleetFromEsiCommandHandler(IFleetRepository repository)
    : ICommandHandler<UncoupleFleetFromEsiCommand, Result>
{
    public async Task<Result> Handle(UncoupleFleetFromEsiCommand command, CancellationToken cancellationToken = default)
    {
        var fleet = await repository.GetAsync(command.FleetId, cancellationToken);
        if (fleet is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        // Owner-only: coupling binds the plan to a real fleet, so only its creator may break the link.
        if (fleet.CreatorCharacterId != command.ActingCharacterId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed,
                "Only the fleet owner can uncouple it from the in-game fleet.", "Fleet"));

        // Idempotent: clearing an already-unlinked fleet is a no-op success — the caller's intent (no stored link) holds.
        fleet.EsiFleetId = null;
        fleet.EsiFleetBossId = null;
        fleet.EsiSyncState = EsiFleetSyncState.NotLinked;
        await repository.UpdateAsync(fleet, cancellationToken);
        return Result.Success();
    }
}
