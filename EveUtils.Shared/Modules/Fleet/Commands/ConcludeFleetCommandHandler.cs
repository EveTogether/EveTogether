using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class ConcludeFleetCommandHandler(IFleetRepository repository, IDispatcher dispatcher)
    : ICommandHandler<ConcludeFleetCommand, Result>
{
    public async Task<Result> Handle(ConcludeFleetCommand command, CancellationToken cancellationToken = default)
    {
        // Creator-only on a fleet that exists and is not archived.
        var resolved = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, command.FleetId, command.ActingCharacterId, cancellationToken);
        if (!resolved.IsSuccess || resolved.Value is not { } fleet)
            return Result.Failure(resolved.Messages.ToArray());

        // Idempotent: an already-concluded fleet succeeds without a second round of notifications.
        if (fleet.Activation == FleetActivation.Concluded)
            return Result.Success();

        // Conclude is the terminal step of an op that actually ran: only an Active fleet can be concluded. A Forming
        // fleet never started, so there is no op to conclude — it is cancelled by disbanding it instead.
        if (fleet.Activation != FleetActivation.Active)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed,
                "Only an active fleet can be concluded; disband a forming fleet instead.", "Fleet"));

        fleet.Activation = FleetActivation.Concluded;
        fleet.LastActivityAt = DateTimeOffset.UtcNow;
        await repository.UpdateAsync(fleet, cancellationToken);

        // Tell each roster member the op is over (the creator pressed Conclude; externals have no inbox/session).
        var members = await repository.ListMembersAsync(fleet.Id, cancellationToken);
        foreach (var member in members)
        {
            if (member.CharacterId == fleet.CreatorCharacterId || member.IsExternal)
                continue;

            var notify = await dispatcher.Send(new EnqueueMessageCommand(
                member.CharacterId,
                fleet.CreatorCharacterId,
                MessageKind.Mail,
                $"Fleet concluded: {fleet.Name}",
                $"{fleet.Name} has concluded.",
                null,
                null), cancellationToken);
            if (!notify.IsSuccess)
                return Result.Failure(notify.Messages.ToArray());
        }

        return Result.Success();
    }
}
