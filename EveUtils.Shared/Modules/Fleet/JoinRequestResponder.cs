using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Fleet;

/// <summary>
/// The shared core of answering a request-to-join an invite-only fleet: owner-auth → on accept add the
/// requester to the roster, on decline skip it, then notify the requester through the message queue, and mark the
/// request answered. Both response paths reuse this single seam so the logic lives in one place (no duplication):
/// the message path (<see cref="FleetJoinRequestResponder"/>, via the generic RespondToMessage) and the direct
/// <c>RespondToJoinRequestCommand</c>. Auto-registered via the <see cref="IScopedService"/> marker.
/// </summary>
public sealed class JoinRequestResponder(IFleetRepository repository, IDispatcher dispatcher) : IScopedService
{
    public async Task<Result> RespondAsync(long requestId, bool accept, int actingCharacterId, CancellationToken cancellationToken = default)
    {
        var request = await repository.GetJoinRequestAsync(requestId, cancellationToken);
        if (request is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Join request not found.", "Fleet"));

        if (request.Status != FleetJoinRequestStatus.Pending)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Join request already responded to.", "Fleet"));

        var fleet = await repository.GetAsync(request.FleetId, cancellationToken);
        if (fleet is null)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        // Only the fleet owner may answer a join request (the creator is the sole authority).
        if (fleet.CreatorCharacterId != actingCharacterId)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "Only the fleet's creator can answer a join request.", "Fleet"));

        // Accepting adds the requester to the roster, so the one-active-fleet/concluded guard applies to them. The
        // request stays Pending on a block, so the owner can retry once the requester has left their other fleet.
        if (accept)
        {
            var joinable = await ActiveFleetMembershipGuard.EnsureJoinableAsync(repository, fleet, request.RequesterCharacterId, cancellationToken);
            if (!joinable.IsSuccess)
                return joinable;
        }

        request.Status = accept ? FleetJoinRequestStatus.Accepted : FleetJoinRequestStatus.Denied;
        request.RespondedAt = DateTimeOffset.UtcNow;
        await repository.UpdateJoinRequestAsync(request, cancellationToken);

        if (accept && !await repository.IsMemberAsync(request.FleetId, request.RequesterCharacterId, cancellationToken))
        {
            // EVE parity (2026-06-04): place the accepted requester like any joiner — first open squad, auto-creating
            // the next squad/wing when full — rather than dropping them unassigned.
            var (wingId, squadId) = await FleetMemberPlacement.ResolveOrCreateSquadAsync(repository, request.FleetId, cancellationToken);
            await repository.AddMemberAsync(new FleetMember
            {
                FleetId = request.FleetId,
                CharacterId = request.RequesterCharacterId,
                WingId = wingId,
                SquadId = squadId,
                Role = FleetRole.SquadMember,
                JoinTime = DateTimeOffset.UtcNow
            }, cancellationToken);
        }

        // Notify the requester of the outcome through the queue (plain mail, no response).
        var notify = await dispatcher.Send(new EnqueueMessageCommand(
            request.RequesterCharacterId,
            actingCharacterId,
            MessageKind.Mail,
            accept ? $"Join request accepted: {fleet.Name}" : $"Join request declined: {fleet.Name}",
            accept
                ? $"Your request to join {fleet.Name} was accepted."
                : $"Your request to join {fleet.Name} was declined.",
            null,
            null), cancellationToken);
        if (!notify.IsSuccess)
            return Result.Failure(notify.Messages.ToArray());

        return Result.Success();
    }
}
