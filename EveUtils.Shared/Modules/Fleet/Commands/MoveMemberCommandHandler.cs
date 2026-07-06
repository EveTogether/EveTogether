using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class MoveMemberCommandHandler(IFleetRepository repository)
    : ICommandHandler<MoveMemberCommand, Result>
{
    public async Task<Result> Handle(MoveMemberCommand command, CancellationToken cancellationToken = default)
    {
        var member = await repository.GetMemberAsync(command.MemberId, cancellationToken);
        if (member is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet member not found.", "Fleet"));

        var owned = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, member.FleetId, command.ActingCharacterId, cancellationToken);
        if (!owned.IsSuccess)
            return Result.Failure(owned.Messages.ToArray());

        // ESI position rules per role (§6 move-endpoint): which of wing/squad must be set.
        var (wingId, squadId) = NormalizePosition(command.Role, command.WingId, command.SquadId);
        var positionError = ValidatePosition(command.Role, wingId, squadId);
        if (positionError is not null)
            return Result.Failure(positionError);

        // Referential integrity: the wing belongs to the fleet, the squad to the wing.
        if (wingId != -1)
        {
            var wing = await repository.GetWingAsync(wingId, cancellationToken);
            if (wing is null || wing.FleetId != member.FleetId)
                return Result.Failure(new ResultMessage(
                    MessageSeverity.Error, MessageCodes.ValidationFailed, "Target wing does not belong to this fleet.", "Fleet"));
        }

        if (squadId != -1)
        {
            var squad = await repository.GetSquadAsync(squadId, cancellationToken);
            if (squad is null || squad.WingId != wingId)
                return Result.Failure(new ResultMessage(
                    MessageSeverity.Error, MessageCodes.ValidationFailed, "Target squad does not belong to the target wing.", "Fleet"));
        }

        var roster = await repository.ListMembersAsync(member.FleetId, cancellationToken);

        // Squad capacity (EVE: 10 per squad, the SC counted).
        if (squadId != -1 && member.SquadId != squadId)
        {
            var occupancy = roster.Count(m => m.Id != member.Id && m.SquadId == squadId);
            if (occupancy >= FleetStructureLimits.MaxMembersPerSquad)
                return Result.Failure(new ResultMessage(
                    MessageSeverity.Error, MessageCodes.ValidationFailed,
                    $"A squad holds at most {FleetStructureLimits.MaxMembersPerSquad} members.", "Fleet"));
        }

        // Command-position uniqueness (EVE: one FC per fleet, one WC per wing, one SC per squad).
        var conflict = CommandSlotConflict(command.Role, member, wingId, squadId, roster);
        if (conflict is not null)
            return Result.Failure(conflict);

        member.Role = command.Role;
        member.WingId = wingId;
        member.SquadId = squadId;
        await repository.UpdateMemberAsync(member, cancellationToken);

        return Result.Success();
    }

    private static (long WingId, long SquadId) NormalizePosition(FleetRole role, long wingId, long squadId) =>
        role switch
        {
            FleetRole.FleetCommander => (-1, -1),
            FleetRole.Unassigned => (-1, -1),
            FleetRole.WingCommander => (wingId, -1),
            _ => (wingId, squadId)
        };

    private static ResultMessage? ValidatePosition(FleetRole role, long wingId, long squadId)
    {
        var ok = role switch
        {
            FleetRole.FleetCommander => wingId == -1 && squadId == -1,
            // Unassigned (R3-5): in the fleet without a position — fleet-level, no wing/squad.
            FleetRole.Unassigned => wingId == -1 && squadId == -1,
            FleetRole.WingCommander => wingId != -1 && squadId == -1,
            FleetRole.SquadCommander => wingId != -1 && squadId != -1,
            FleetRole.SquadMember => wingId != -1 && squadId != -1,
            _ => false
        };

        return ok
            ? null
            : new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "The wing/squad position is invalid for the chosen role.", "Fleet");
    }

    private static ResultMessage? CommandSlotConflict(
        FleetRole role, FleetMember member, long wingId, long squadId, IReadOnlyList<FleetMember> roster)
    {
        var taken = role switch
        {
            FleetRole.FleetCommander => roster.Any(m => m.Id != member.Id && m.Role == FleetRole.FleetCommander),
            FleetRole.WingCommander => roster.Any(m => m.Id != member.Id && m.Role == FleetRole.WingCommander && m.WingId == wingId),
            FleetRole.SquadCommander => roster.Any(m => m.Id != member.Id && m.Role == FleetRole.SquadCommander && m.SquadId == squadId),
            _ => false
        };

        return taken
            ? new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "That command position is already filled.", "Fleet")
            : null;
    }
}
