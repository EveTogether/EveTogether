using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Messaging;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Fleet;

/// <summary>
/// Plugs the commander's switch request into the generic message system (<see cref="IMessageResponder"/>, ET-168).
/// A FleetSwitchRequest-kind message carries the fleet to come to in <c>RefId</c>, and the character answering it is
/// the member who was asked — <c>RespondToMessageCommandHandler</c> has already checked that they are the recipient,
/// so nobody can answer this on someone else's behalf.
///
/// <para>Three answers, and only two of them are a response. <b>Yes</b> delegates to
/// <see cref="SwitchToFleetCommand"/>, which does the leaving and the coupling as one act. <b>No</b> succeeds and
/// changes nothing: the member stays on the roster, simply not linked, and next week the fleet is there with them
/// on it. <b>Later</b> is not an answer at all — the message is left alone, and because a kind with a responder is
/// kept rather than dropped after delivery, it keeps standing for as long as the fleet runs.</para>
///
/// Auto-registered via the <see cref="IScopedService"/> marker, the same way
/// <see cref="FleetInviteMessageResponder"/> is — and registering it is also what tells
/// <c>MessageDeliveryService</c> to keep the message instead of deleting it once pushed.
/// </summary>
public sealed class FleetSwitchRequestResponder(IDispatcher dispatcher) : IMessageResponder, IScopedService
{
    public MessageKind Kind => MessageKind.FleetSwitchRequest;

    public async Task<Result> RespondAsync(QueuedMessage message, bool accept, int actingCharacterId, CancellationToken cancellationToken = default)
    {
        if (message.RefId is not { } fleetId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Switch request has no linked fleet.", "Fleet"));

        // "No, I'll stay where I am." Nothing to do — declining is not leaving, and the roster is untouched on
        // purpose so that a no today does not close the door on next week.
        if (!accept)
            return Result.Success();

        var switched = await dispatcher.Send(new SwitchToFleetCommand(fleetId, actingCharacterId), cancellationToken);
        return switched.IsSuccess ? Result.Success() : Result.Failure(switched.Messages.ToArray());
    }
}
