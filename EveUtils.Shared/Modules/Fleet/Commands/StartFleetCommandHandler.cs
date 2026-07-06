using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class StartFleetCommandHandler(IFleetRepository repository, IDispatcher dispatcher)
    : ICommandHandler<StartFleetCommand, Result>
{
    public async Task<Result> Handle(StartFleetCommand command, CancellationToken cancellationToken = default)
    {
        // Creator-only on a fleet that exists and is not archived.
        var resolved = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, command.FleetId, command.ActingCharacterId, cancellationToken);
        if (!resolved.IsSuccess || resolved.Value is not { } fleet)
            return Result.Failure(resolved.Messages.ToArray());

        // A concluded fleet is finished — it cannot be started again (re-run it by creating a new fleet).
        if (fleet.Activation == FleetActivation.Concluded)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Cannot start a concluded fleet.", "Fleet"));

        // Idempotent: an already-started fleet succeeds without a second round of notifications.
        if (fleet.Activation == FleetActivation.Active)
            return Result.Success();

        var now = DateTimeOffset.UtcNow;
        fleet.Activation = FleetActivation.Active;
        fleet.ActivatedAt = now; // newest activation → the broadcast tiebreak keeps a conflicted member in their earlier fleet
        fleet.LastActivityAt = now;
        await repository.UpdateAsync(fleet, cancellationToken);

        // Notify each roster member that the fleet has started. Metrics are shared automatically while you are a
        // connected member — no "enter" step — so the message just announces the start.
        // The creator is skipped (they pressed Start); external members have no inbox/session.
        // A member already active in another fleet is *not* coupled to this one (one active fleet per character):
        // they keep broadcasting only to the fleet they were activated in first, get a heads-up instead of the start
        // notice, and the creator gets a summary of how many were already busy elsewhere.
        var members = await repository.ListMembersAsync(fleet.Id, cancellationToken);
        var alreadyActiveElsewhere = 0;
        foreach (var member in members)
        {
            if (member.CharacterId == fleet.CreatorCharacterId || member.IsExternal)
                continue;

            var elsewhere = (await repository.ListActiveMembershipsAsync(member.CharacterId, cancellationToken))
                .FirstOrDefault(m => m.FleetId != fleet.Id);

            var notify = elsewhere is not null
                ? await dispatcher.Send(new EnqueueMessageCommand(
                    member.CharacterId, fleet.CreatorCharacterId, MessageKind.Mail,
                    $"Already in an active fleet: {fleet.Name}",
                    $"{fleet.Name} has started, but you are still in active fleet '{elsewhere.FleetName}'. You will not share metrics here until you leave that fleet.",
                    null, null), cancellationToken)
                : await dispatcher.Send(new EnqueueMessageCommand(
                    member.CharacterId, fleet.CreatorCharacterId, MessageKind.FleetStarted,
                    $"Fleet started: {fleet.Name}",
                    $"{fleet.Name} has started — open its metrics to see the fleet live.",
                    null, fleet.Id), cancellationToken); // RefId = fleet id → the client's "open metrics" toast target
            if (!notify.IsSuccess)
                return Result.Failure(notify.Messages.ToArray());

            if (elsewhere is not null)
                alreadyActiveElsewhere++;
        }

        if (alreadyActiveElsewhere > 0)
        {
            var summary = await dispatcher.Send(new EnqueueMessageCommand(
                fleet.CreatorCharacterId, fleet.CreatorCharacterId, MessageKind.FleetStarted,
                $"Fleet started: {fleet.Name}",
                $"{fleet.Name} has started. {alreadyActiveElsewhere} member(s) were already in another active fleet and will not share metrics here until they leave it.",
                null, fleet.Id), cancellationToken); // RefId = fleet id → the client's "open metrics" toast target
            if (!summary.IsSuccess)
                return Result.Failure(summary.Messages.ToArray());
        }

        return Result.Success();
    }
}
