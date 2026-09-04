using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class SetFleetMemberAvailabilityCommandHandler(IFleetRepository repository)
    : ICommandHandler<SetFleetMemberAvailabilityCommand, Result>
{
    public async Task<Result> Handle(SetFleetMemberAvailabilityCommand command, CancellationToken cancellationToken = default)
    {
        var member = await repository.GetMemberAsync(command.MemberId, cancellationToken);
        if (member is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet member not found.", "Fleet"));

        // Self-only, and strictly so: unlike every other roster command in this module, the fleet's own
        // creator has no standing here either — availability is the member's own signal about themselves.
        if (member.CharacterId != command.ActingCharacterId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied,
                "Only the pilot's own client can set their availability.", "Fleet"));

        // An external member has no client — nothing could ever satisfy the self-only check above for them,
        // but a direct, readable refusal beats a PermissionDenied that reads like a bug.
        if (member.IsExternal)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed,
                "External members have no client and cannot set their availability.", "Fleet"));

        var fleet = await repository.GetAsync(member.FleetId, cancellationToken);
        if (fleet is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        // Signing off answers "will you be there next time this starts" — meaningless once it already has,
        // or once it never will again.
        if (fleet.Activation != FleetActivation.Forming)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed,
                "Availability can only be set before the fleet starts.", "Fleet"));

        member.Availability = command.Availability;
        member.AvailabilityNote = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        member.AvailabilityUpdatedAt = DateTimeOffset.UtcNow;
        await repository.UpdateMemberAsync(member, cancellationToken);

        return Result.Success();
    }
}
