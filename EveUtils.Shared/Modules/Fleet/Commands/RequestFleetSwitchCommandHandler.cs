using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Queries;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class RequestFleetSwitchCommandHandler(IFleetRepository repository, IDispatcher dispatcher)
    : ICommandHandler<RequestFleetSwitchCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RequestFleetSwitchCommand command, CancellationToken cancellationToken = default)
    {
        // Creator-only on a fleet that exists and is not archived — the same gate START runs.
        var resolved = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, command.FleetId, command.ActingCharacterId, cancellationToken);
        if (!resolved.IsSuccess || resolved.Value is not { } fleet)
            return Result<int>.Failure(resolved.Messages.ToArray());

        // "Come over" only means something once there is something to come over to. A fleet standing by has nobody
        // linked to it, so a request sent now would ask a member to leave a running fleet for one that is not.
        if (fleet.Activation != FleetActivation.Active)
            return Result<int>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed,
                "Start the fleet first — a fleet that is standing by has nothing to switch to.", "Fleet"));

        var elsewhere = await dispatcher.Query(new ListMembersActiveElsewhereQuery(fleet.Id), cancellationToken);

        var asked = 0;
        foreach (var member in elsewhere)
        {
            if (command.OnlyCharacterId is { } only && member.CharacterId != only)
                continue;

            // The commander is never asked to switch to their own fleet: if they are counting for an earlier fleet
            // of their own, stopping that one is theirs to do and this request would be them nudging themselves.
            if (member.CharacterId == fleet.CreatorCharacterId)
                continue;

            // RefId = the fleet to come to. The message stays answerable until it is answered or expires, so
            // "later" is a real answer: the request keeps standing while the fleet runs, and a member who switches
            // an hour on still joins in.
            var sent = await dispatcher.Send(new EnqueueMessageCommand(
                member.CharacterId, fleet.CreatorCharacterId, MessageKind.FleetSwitchRequest,
                $"We have started — are you coming? {fleet.Name}",
                $"You are on {fleet.Name}'s roster, but you are sharing with '{member.ElsewhereFleetName}'. "
                + $"While that is so you do not count here. Switching leaves {member.ElsewhereFleetName} and links you to {fleet.Name}; "
                + "staying where you are keeps you on this roster, just not linked.",
                null, fleet.Id), cancellationToken);
            if (!sent.IsSuccess)
                return Result<int>.Failure(sent.Messages.ToArray());

            asked++;
        }

        return Result<int>.Success(asked);
    }
}
