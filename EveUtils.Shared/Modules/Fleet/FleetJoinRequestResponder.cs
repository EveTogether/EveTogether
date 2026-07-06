using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Messaging;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Fleet;

/// <summary>
/// Plugs the request-to-join response into the generic message system (<see cref="IMessageResponder"/>).
/// A FleetJoinRequest-kind message carries the durable request id in <c>RefId</c>; the acting
/// character answering it is the fleet owner. The domain action (owner-auth → roster + requester notification +
/// status) lives once in <see cref="JoinRequestResponder"/>, shared with the direct
/// <c>RespondToJoinRequestCommand</c> path — this responder only maps the message envelope onto that core.
/// Auto-registered via the <see cref="IScopedService"/> marker, the same way
/// <see cref="FleetInviteMessageResponder"/> is.
/// </summary>
public sealed class FleetJoinRequestResponder(JoinRequestResponder responder) : IMessageResponder, IScopedService
{
    public MessageKind Kind => MessageKind.FleetJoinRequest;

    public async Task<Result> RespondAsync(QueuedMessage message, bool accept, int actingCharacterId, CancellationToken cancellationToken = default)
    {
        if (message.RefId is not { } requestId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Join-request message has no linked request.", "Fleet"));

        return await responder.RespondAsync(requestId, accept, actingCharacterId, cancellationToken);
    }
}
