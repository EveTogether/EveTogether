using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class AssignMemberFitCommandHandler(IFleetRepository repository)
    : ICommandHandler<AssignMemberFitCommand, Result>
{
    public async Task<Result> Handle(AssignMemberFitCommand command, CancellationToken cancellationToken = default)
    {
        var member = await repository.GetMemberAsync(command.MemberId, cancellationToken);
        if (member is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet member not found.", "Fleet"));

        // The creator may assign top-down, or the member may set their OWN fit — every pilot
        // picks their own ship, so this is owner-OR-self rather than the creator-only structure guard.
        var owned = await FleetStructureGuard.ResolveFleetForMemberFitAsync(
            repository, member.FleetId, command.ActingCharacterId, member.CharacterId, cancellationToken);
        if (!owned.IsSuccess)
            return Result.Failure(owned.Messages.ToArray());

        member.AssignedFit = command.Fit;
        member.AssignedCompositionEntryId = command.Fit is null ? null : command.CompositionEntryId;
        // A new or cleared fit invalidates the pilot-reported can-fly verdict — their client re-reports
        // against the new fit when it sees the change.
        member.FitSkillVerdict = FitSkillVerdict.Unknown;
        await repository.UpdateMemberAsync(member, cancellationToken);

        return Result.Success();
    }
}
