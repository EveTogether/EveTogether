using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class AddLocalCharacterCommandHandler(IFleetRepository repository, IEventBus eventBus)
    : ICommandHandler<AddLocalCharacterCommand, Result<long>>
{
    public async Task<Result<long>> Handle(AddLocalCharacterCommand command, CancellationToken cancellationToken = default)
    {
        if (command.CharacterId <= 0)
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "A valid character is required.", "Fleet"));

        var owned = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, command.FleetId, command.ActingCharacterId, cancellationToken);
        if (!owned.IsSuccess)
            return Result<long>.Failure(owned.Messages.ToArray());

        var fleetId = command.FleetId;
        if (await repository.IsMemberAsync(fleetId, command.CharacterId, cancellationToken))
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "That character is already a member.", "Fleet"));

        var members = await repository.ListMembersAsync(fleetId, cancellationToken);
        if (members.Count >= FleetStructureLimits.MaxFleetSize)
            return Result<long>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Fleet is full.", "Fleet"));

        var (wingId, squadId) = await _FirstOpenSquadAsync(fleetId, members, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var memberId = await repository.AddMemberAsync(new FleetMember
        {
            FleetId = fleetId,
            CharacterId = command.CharacterId,
            Role = FleetRole.SquadMember,
            WingId = wingId,
            SquadId = squadId,
            JoinTime = now,
            IsExternal = false
        }, cancellationToken);

        // A roster change is a member event — bump the activity clock so the cleanup grace resets.
        await repository.TouchActivityAsync(fleetId, now, cancellationToken);

        await eventBus.PublishAsync(
            new FleetChangedEvent(new FleetChangePayload(fleetId, FleetChangeKind.RosterChanged)) { ActingCharacterId = command.ActingCharacterId },
            EventTarget.Local, cancellationToken);
        return Result<long>.Success(memberId);
    }

    private async Task<(long WingId, long SquadId)> _FirstOpenSquadAsync(
        long fleetId, IReadOnlyList<FleetMember> roster, CancellationToken cancellationToken)
    {
        foreach (var wing in await repository.ListWingsAsync(fleetId, cancellationToken)) // Id-ordered
        {
            foreach (var squad in await repository.ListSquadsAsync(wing.Id, cancellationToken)) // Id-ordered
            {
                if (roster.Count(member => member.SquadId == squad.Id) < FleetStructureLimits.MaxMembersPerSquad)
                    return (wing.Id, squad.Id);
            }
        }

        return (-1, -1); // ESI "unassigned" sentinel — leave the owner to place them manually.
    }
}
