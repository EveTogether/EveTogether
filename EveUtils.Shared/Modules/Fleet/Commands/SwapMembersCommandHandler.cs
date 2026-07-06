using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class SwapMembersCommandHandler(IFleetRepository repository)
    : ICommandHandler<SwapMembersCommand, Result>
{
    public async Task<Result> Handle(SwapMembersCommand command, CancellationToken cancellationToken = default)
    {
        // Swapping a member with itself changes nothing.
        if (command.FirstMemberId == command.SecondMemberId)
            return Result.Success();

        var first = await repository.GetMemberAsync(command.FirstMemberId, cancellationToken);
        var second = await repository.GetMemberAsync(command.SecondMemberId, cancellationToken);
        if (first is null || second is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet member not found.", "Fleet"));

        if (first.FleetId != second.FleetId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Members must be in the same fleet to swap.", "Fleet"));

        var owned = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, first.FleetId, command.ActingCharacterId, cancellationToken);
        if (!owned.IsSuccess)
            return Result.Failure(owned.Messages.ToArray());

        // Exchange the two members' exact positions (role + wing + squad). Both positions were already valid and
        // command-slot-unique, so a 1:1 exchange stays consistent — no re-validation needed. The fit each pilot flies
        // stays with the pilot (only the position moves).
        (first.Role, second.Role) = (second.Role, first.Role);
        (first.WingId, second.WingId) = (second.WingId, first.WingId);
        (first.SquadId, second.SquadId) = (second.SquadId, first.SquadId);

        await repository.UpdateMembersAsync(first, second, cancellationToken);
        return Result.Success();
    }
}
