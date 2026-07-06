using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class ReportMemberInGameFleetCommandHandler(IFleetRepository repository)
    : ICommandHandler<ReportMemberInGameFleetCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(ReportMemberInGameFleetCommand command, CancellationToken cancellationToken = default)
    {
        var member = await repository.GetMemberAsync(command.MemberId, cancellationToken);
        if (member is null)
            return Result<bool>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet member not found.", "Fleet"));

        // Self-only: a pilot's in-game presence is read from their own /characters/{id}/fleet/, so only that client may
        // report it — not the owner, not another member.
        if (member.CharacterId != command.ActingCharacterId)
            return Result<bool>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied,
                "Only the pilot's own client can report their in-game fleet presence.", "Fleet"));

        // ESI keys a fleet member by character id, so a confirmed presence records the character id (the EsiMemberId
        // seam); a retraction clears it. Unchanged → not a change (idempotent), so the caller broadcasts nothing.
        var esiMemberId = command.InFleet ? (long?)member.CharacterId : null;
        if (member.EsiMemberId == esiMemberId)
            return Result<bool>.Success(false);

        member.EsiMemberId = esiMemberId;
        await repository.UpdateMemberAsync(member, cancellationToken);
        return Result<bool>.Success(true);
    }
}
