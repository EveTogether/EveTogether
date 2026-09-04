using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class StopFleetCommandHandler(IFleetRepository repository, IDispatcher dispatcher)
    : ICommandHandler<StopFleetCommand, Result>
{
    public async Task<Result> Handle(StopFleetCommand command, CancellationToken cancellationToken = default)
    {
        // Creator-only on a fleet that exists and is not archived — the same guard Start and Conclude run. The
        // automatic stop (ET-167) comes through here too, with the owner's own id, so this check is passed rather
        // than stepped around.
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

        var automatic = command.Trigger != FleetStopTrigger.Manual;

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
                Title(fleet.Name, command.Trigger),
                MemberBody(fleet.Name, command.Trigger),
                null,
                null), cancellationToken);
            if (!notify.IsSuccess)
                return Result.Failure(notify.Messages.ToArray());
        }

        // On an automatic stop the owner is told as well, and this is not symmetry for its own sake: on a manual stop
        // they are the one who pressed the button, but here the fleet stood itself down while they may not even have
        // had a client running. They are also the only one told at all when the ground was an empty roster.
        if (automatic)
        {
            var owner = await dispatcher.Send(new EnqueueMessageCommand(
                fleet.CreatorCharacterId,
                fleet.CreatorCharacterId,
                MessageKind.Mail,
                Title(fleet.Name, command.Trigger),
                OwnerBody(fleet.Name, command.Trigger),
                null,
                null), cancellationToken);
            if (!owner.IsSuccess)
                return Result.Failure(owner.Messages.ToArray());
        }

        return Result.Success();
    }

    private static string Title(string fleetName, FleetStopTrigger trigger) =>
        trigger == FleetStopTrigger.Manual
            ? $"Fleet stopped: {fleetName}"
            : $"Fleet stopped automatically: {fleetName}";

    private static string MemberBody(string fleetName, FleetStopTrigger trigger) => trigger switch
    {
        FleetStopTrigger.RosterEmpty =>
            $"{fleetName} stopped on its own because no one was left on its roster. It is standing by and can be started again.",
        FleetStopTrigger.AllMembersOffline =>
            $"{fleetName} stopped on its own because every member had gone offline. It is standing by and can be started again — you are still on its roster.",
        _ =>
            $"{fleetName} has stopped and is standing by again. You are still on its roster and free to fly in another fleet until it starts again.",
    };

    private static string OwnerBody(string fleetName, FleetStopTrigger trigger) => trigger switch
    {
        FleetStopTrigger.RosterEmpty =>
            $"{fleetName} stopped on its own because no one was left on its roster. Nothing is lost — it is standing by with its roster and doctrine, ready to start again.",
        _ =>
            $"{fleetName} stopped on its own because every member had gone offline. Nothing is lost — it is standing by with its roster and doctrine, ready to start again.",
    };
}
