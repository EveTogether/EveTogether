using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class RequestToJoinCommandHandler(IFleetRepository repository, IDispatcher dispatcher)
    : ICommandHandler<RequestToJoinCommand, Result<FleetJoinRequestPayload>>
{
    public async Task<Result<FleetJoinRequestPayload>> Handle(RequestToJoinCommand command, CancellationToken cancellationToken = default)
    {
        var fleet = await repository.GetAsync(command.FleetId, cancellationToken);
        if (fleet is null)
            return Result<FleetJoinRequestPayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Fleet not found.", "Fleet"));

        if (fleet.State != FleetState.Active)
            return Result<FleetJoinRequestPayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Fleet is not active.", "Fleet"));

        // A public fleet is joinable directly — there is no one to ask. Only invite-only fleets gate
        // entry behind the owner, so only those take a request-to-join.
        if (fleet.Visibility != FleetVisibility.InviteOnly)
            return Result<FleetJoinRequestPayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "This fleet is public — join it directly.", "Fleet"));

        if (fleet.CreatorCharacterId == command.ActingCharacterId)
            return Result<FleetJoinRequestPayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "You already own this fleet.", "Fleet"));

        if (await repository.IsMemberAsync(fleet.Id, command.ActingCharacterId, cancellationToken))
            return Result<FleetJoinRequestPayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "You are already a fleet member.", "Fleet"));

        if (await repository.HasPendingJoinRequestAsync(fleet.Id, command.ActingCharacterId, cancellationToken))
            return Result<FleetJoinRequestPayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "You already have a pending request to join this fleet.", "Fleet"));

        var requestId = await repository.AddJoinRequestAsync(new FleetJoinRequest
        {
            FleetId = fleet.Id,
            RequesterCharacterId = command.ActingCharacterId,
            Status = FleetJoinRequestStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

        var payload = new FleetJoinRequestPayload(requestId, fleet.Id, fleet.Name, command.ActingCharacterId);

        // the owner is notified through the message queue (single inbox channel). Enqueue a
        // FleetJoinRequest-kind envelope linked to the durable request via RefId; the owner answers it with the
        // generic RespondToMessage, which delegates to the FleetJoinRequestResponder.
        var enqueue = await dispatcher.Send(new EnqueueMessageCommand(
            fleet.CreatorCharacterId,
            command.ActingCharacterId,
            MessageKind.FleetJoinRequest,
            $"Join request: {fleet.Name}",
            null,
            null,
            requestId), cancellationToken);
        if (!enqueue.IsSuccess)
            return Result<FleetJoinRequestPayload>.Failure(enqueue.Messages.ToArray());

        return Result<FleetJoinRequestPayload>.Success(payload);
    }
}
