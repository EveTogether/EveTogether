using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class ReportMemberFitVerdictCommandHandler(IFleetRepository repository)
    : ICommandHandler<ReportMemberFitVerdictCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(ReportMemberFitVerdictCommand command, CancellationToken cancellationToken = default)
    {
        var member = await repository.GetMemberAsync(command.MemberId, cancellationToken);
        if (member is null)
            return Result<bool>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet member not found.", "Fleet"));

        // Self-only: trained skills live on the pilot's own client, so only that client may speak for them —
        // not the owner, not another member.
        if (member.CharacterId != command.ActingCharacterId)
            return Result<bool>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied,
                "Only the pilot's own client can report their skill verdict.", "Fleet"));

        // A verdict only means something against an assigned fit; without one it collapses to Unknown.
        var verdict = member.AssignedFit is null ? FitSkillVerdict.Unknown : command.Verdict;
        if (member.FitSkillVerdict == verdict)
            return Result<bool>.Success(false);   // idempotent — a re-report of the same verdict is not a change

        member.FitSkillVerdict = verdict;
        await repository.UpdateMemberAsync(member, cancellationToken);
        return Result<bool>.Success(true);
    }
}
