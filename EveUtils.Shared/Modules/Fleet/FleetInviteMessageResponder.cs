using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.ServerAuth.Repositories;

namespace EveUtils.Shared.Modules.Fleet;

/// <summary>
/// Plugs the fleet-invite response into the generic message system (seam, <see cref="IMessageResponder"/>).
/// A FleetInvite-kind message carries the durable invite id in <c>RefId</c>; answering it delegates to
/// <see cref="RespondToFleetInviteCommand"/> so the roster logic lives in exactly one place and
/// --fleet-invite-test stays green. On a successful response the inviter is notified of the outcome via plain
/// mail — the same way <see cref="FleetJoinRequestResponder"/> notifies the requester. Auto-registered
/// via the <see cref="IScopedService"/> marker.
/// </summary>
public sealed class FleetInviteMessageResponder(
    IFleetRepository repository, IDispatcher dispatcher, IServerAuthRepository serverAuthRepository)
    : IMessageResponder, IScopedService
{
    public MessageKind Kind => MessageKind.FleetInvite;

    public async Task<Result> RespondAsync(QueuedMessage message, bool accept, int actingCharacterId, CancellationToken cancellationToken = default)
    {
        if (message.RefId is not { } inviteId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Invite message has no linked invite.", "Fleet"));

        // Snapshot the invite up front so the inviter id and fleet name survive the response mutation below.
        var invite = await repository.GetInviteAsync(inviteId, cancellationToken);

        var result = await dispatcher.Send(new RespondToFleetInviteCommand(inviteId, accept, actingCharacterId), cancellationToken);
        if (!result.IsSuccess)
            return Result.Failure(result.Messages.ToArray());

        // Notify the inviter of the outcome through the queue (plain mail, no response). The live push to the
        // inviter rides the existing message-delivery path.
        if (invite is not null)
        {
            var fleet = await repository.GetAsync(invite.FleetId, cancellationToken);
            var fleetName = fleet?.Name ?? "the fleet";

            // Resolve the responder's display name from the server's synced characters (the acceptor is connected,
            // so the server knows their name) and fall back to the id only when it is unknown.
            var synced = await serverAuthRepository.ListSyncedAsync(cancellationToken);
            var actorName = synced.FirstOrDefault(c => c.EsiCharacterId == actingCharacterId)?.CharacterName;
            var actor = string.IsNullOrWhiteSpace(actorName) ? actingCharacterId.ToString() : actorName;

            var notify = await dispatcher.Send(new EnqueueMessageCommand(
                invite.InviterCharacterId,
                actingCharacterId,
                MessageKind.Mail,
                accept ? $"Invite accepted: {fleetName}" : $"Invite declined: {fleetName}",
                accept
                    ? $"{actor} accepted your invite to {fleetName}."
                    : $"{actor} declined your invite to {fleetName}.",
                null,
                null), cancellationToken);
            if (!notify.IsSuccess)
                return Result.Failure(notify.Messages.ToArray());
        }

        return Result.Success();
    }
}
