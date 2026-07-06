using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class RemoveFleetMemberCommandHandler(IFleetRepository repository)
    : ICommandHandler<RemoveFleetMemberCommand, Result>
{
    public async Task<Result> Handle(RemoveFleetMemberCommand command, CancellationToken cancellationToken = default)
    {
        var member = await repository.GetMemberAsync(command.MemberId, cancellationToken);
        if (member is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet member not found.", "Fleet"));

        var fleet = await repository.GetAsync(member.FleetId, cancellationToken);
        if (fleet is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        // The owner may remove anyone; otherwise a member may only remove themselves (leave).
        var isOwner = fleet.CreatorCharacterId == command.ActingCharacterId;
        var isSelf = member.CharacterId == command.ActingCharacterId;
        if (!isOwner && !isSelf)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "You can only remove yourself from the fleet.", "Fleet"));

        // The creator can never be removed — by anyone — until ownership moves to someone else first.
        if (member.CharacterId == fleet.CreatorCharacterId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Transfer ownership before the creator can leave the fleet.", "Fleet"));

        await repository.RemoveMemberAsync(member.Id, cancellationToken);

        // A roster change is a member event — bump the activity clock so the cleanup grace resets.
        await repository.TouchActivityAsync(fleet.Id, DateTimeOffset.UtcNow, cancellationToken);

        return Result.Success();
    }
}
