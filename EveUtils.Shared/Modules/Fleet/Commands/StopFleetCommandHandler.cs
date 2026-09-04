using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class StopFleetCommandHandler(IFleetRepository repository, IDispatcher dispatcher)
    : ICommandHandler<StopFleetCommand, Result>
{
    public async Task<Result> Handle(StopFleetCommand command, CancellationToken cancellationToken = default)
    {
        // Creator-only on a fleet that exists and is not archived — the same guard Start and Conclude run.
        var resolved = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, command.FleetId, command.ActingCharacterId, cancellationToken);
        if (!resolved.IsSuccess || resolved.Value is not { } fleet)
            return Result.Failure(resolved.Messages.ToArray());

        // A concluded fleet is finished; stopping is the way back from Active, not a way out of the terminal state.
        if (fleet.Activation == FleetActivation.Concluded)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Cannot stop a concluded fleet.", "Fleet"));

        // Idempotent: a fleet that is already standing by succeeds without a second round of notifications.
        if (fleet.Activation == FleetActivation.Forming)
            return Result.Success();

        // Back to Forming, roster and all. ActivatedAt is left where it is: it records the last activation, the next
        // Start overwrites it, and nothing reads it off a fleet that is not Active.
        fleet.Activation = FleetActivation.Forming;
        fleet.LastActivityAt = DateTimeOffset.UtcNow;
        await repository.UpdateAsync(fleet, cancellationToken);

        // Tell each roster member the op is over for now — they keep their seat but are free to fly elsewhere. The
        // creator is skipped (they pressed Stop); external members have no inbox/session.
        var members = await repository.ListMembersAsync(fleet.Id, cancellationToken);
        foreach (var member in members)
        {
            if (member.CharacterId == fleet.CreatorCharacterId || member.IsExternal)
                continue;

            var notify = await dispatcher.Send(new EnqueueMessageCommand(
                member.CharacterId,
                fleet.CreatorCharacterId,
                MessageKind.Mail,
                $"Fleet stopped: {fleet.Name}",
                $"{fleet.Name} has stopped and is standing by again. You are still on its roster and free to fly in another fleet until it starts again.",
                null,
                null), cancellationToken);
            if (!notify.IsSuccess)
                return Result.Failure(notify.Messages.ToArray());
        }

        return Result.Success();
    }
}
