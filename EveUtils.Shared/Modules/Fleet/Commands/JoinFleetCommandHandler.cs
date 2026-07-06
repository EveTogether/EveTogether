using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class JoinFleetCommandHandler(IFleetRepository repository)
    : ICommandHandler<JoinFleetCommand, Result>
{
    public async Task<Result> Handle(JoinFleetCommand command, CancellationToken cancellationToken = default)
    {
        var fleet = await repository.GetAsync(command.FleetId, cancellationToken);
        if (fleet is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        if (fleet.State == FleetState.Archived)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Fleet is no longer active.", "Fleet"));

        if (fleet.Visibility != FleetVisibility.Public)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "Fleet is invite-only; join via an invite.", "Fleet"));

        // Idempotent: the (FleetId, CharacterId) roster index is unique.
        if (await repository.IsMemberAsync(fleet.Id, command.ActingCharacterId, cancellationToken))
            return Result.Success();

        // One active fleet per character + no joining a concluded fleet (2026-06-04).
        var joinable = await ActiveFleetMembershipGuard.EnsureJoinableAsync(repository, fleet, command.ActingCharacterId, cancellationToken);
        if (!joinable.IsSuccess)
            return joinable;

        // EVE parity: drop the joiner into the first squad with room, auto-creating the next squad/wing when
        // every squad is full (shared placement across all join paths, 2026-06-04).
        var (wingId, squadId) = await FleetMemberPlacement.ResolveOrCreateSquadAsync(repository, fleet.Id, cancellationToken);

        await repository.AddMemberAsync(new FleetMember
        {
            FleetId = fleet.Id,
            CharacterId = command.ActingCharacterId,
            Role = FleetRole.SquadMember,
            WingId = wingId,
            SquadId = squadId,
            JoinTime = DateTimeOffset.UtcNow
        }, cancellationToken);

        // A join is a member event — bump the activity clock so the cleanup grace resets.
        await repository.TouchActivityAsync(fleet.Id, DateTimeOffset.UtcNow, cancellationToken);

        return Result.Success();
    }
}
