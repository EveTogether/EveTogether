using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class AddExternalMemberCommandHandler(IFleetRepository repository)
    : ICommandHandler<AddExternalMemberCommand, Result<long>>
{
    public async Task<Result<long>> Handle(AddExternalMemberCommand command, CancellationToken cancellationToken = default)
    {
        if (command.CharacterId <= 0)
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "A valid character is required.", "Fleet"));

        // Creator-only on an existing, still-active fleet (NOT_FOUND / archived / non-creator handled here).
        var owned = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, command.FleetId, command.ActingCharacterId, cancellationToken);
        if (!owned.IsSuccess)
            return Result<long>.Failure(owned.Messages.ToArray());

        var fleet = owned.Value!;

        // Idempotent: the (FleetId, CharacterId) roster index is unique — a character already rostered is left
        // as-is (re-adding an existing member is a no-op success, mirroring JoinFleet).
        if (await repository.IsMemberAsync(fleet.Id, command.CharacterId, cancellationToken))
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "That character is already a member.", "Fleet"));

        // Whole-fleet hard cap: externals count against the same size budget as session-backed members.
        var members = await repository.ListMembersAsync(fleet.Id, cancellationToken);
        if (members.Count >= FleetStructureLimits.MaxFleetSize)
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Fleet is full.", "Fleet"));

        var now = DateTimeOffset.UtcNow;
        var memberId = await repository.AddMemberAsync(new FleetMember
        {
            FleetId = fleet.Id,
            CharacterId = command.CharacterId,
            Role = FleetRole.SquadMember,
            WingId = -1,
            SquadId = -1,
            JoinTime = now,
            IsExternal = true
        }, cancellationToken);

        // A roster change is a member event — bump the activity clock so the cleanup grace resets.
        await repository.TouchActivityAsync(fleet.Id, now, cancellationToken);

        return Result<long>.Success(memberId);
    }
}
