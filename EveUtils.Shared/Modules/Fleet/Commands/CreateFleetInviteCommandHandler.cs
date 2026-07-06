using System.Linq;
using System.Text.Json;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class CreateFleetInviteCommandHandler(IFleetRepository repository, IDispatcher dispatcher)
    : ICommandHandler<CreateFleetInviteCommand, Result<FleetInvitePayload>>
{
    public async Task<Result<FleetInvitePayload>> Handle(CreateFleetInviteCommand command, CancellationToken cancellationToken = default)
    {
        if (command.InviteeCharacterId <= 0)
            return Result<FleetInvitePayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "An invitee character is required.", "Fleet"));

        if (command.InviteeCharacterId == command.ActingCharacterId)
            return Result<FleetInvitePayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "You cannot invite yourself.", "Fleet"));

        var owned = await FleetStructureGuard.ResolveOwnedActiveFleetAsync(
            repository, command.FleetId, command.ActingCharacterId, cancellationToken);
        if (!owned.IsSuccess)
            return Result<FleetInvitePayload>.Failure(owned.Messages.ToArray());

        var fleet = owned.Value!;

        if (await repository.IsMemberAsync(fleet.Id, command.InviteeCharacterId, cancellationToken))
            return Result<FleetInvitePayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Character is already a fleet member.", "Fleet"));

        if (await repository.HasPendingInviteAsync(fleet.Id, command.InviteeCharacterId, cancellationToken))
            return Result<FleetInvitePayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Character already has a pending invite to this fleet.", "Fleet"));

        var inviteId = await repository.AddInviteAsync(new FleetInvite
        {
            FleetId = fleet.Id,
            InviterCharacterId = command.ActingCharacterId,
            InviteeCharacterId = command.InviteeCharacterId,
            Role = command.Role,
            WingId = command.WingId,
            SquadId = command.SquadId,
            Message = string.IsNullOrWhiteSpace(command.Message) ? null : command.Message,
            Status = FleetInviteStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

        var payload = new FleetInvitePayload(
            inviteId, fleet.Id, fleet.Name, command.ActingCharacterId, command.InviteeCharacterId, command.Role);

        // the invite is delivered to the invitee through the message queue (single inbox channel).
        // Enqueue a FleetInvite-kind envelope linked to the durable invite via RefId; the FleetInvite row
        // stays the canonical roster/status source, this is its transport + inbox copy.
        // the body is enriched with the fleet name, the offered role and (when set) the scheduled window,
        // with the inviter's free-text note appended; the Message field on the invite row stays the raw note.
        var body = BuildInviteBody(fleet.Name, command.Role, fleet.FromTime, fleet.ToTime, command.Message);
        var enqueue = await dispatcher.Send(new EnqueueMessageCommand(
            command.InviteeCharacterId,
            command.ActingCharacterId,
            MessageKind.FleetInvite,
            $"Fleet invite: {fleet.Name}",
            body,
            JsonSerializer.Serialize(payload),
            inviteId), cancellationToken);
        if (!enqueue.IsSuccess)
            return Result<FleetInvitePayload>.Failure(enqueue.Messages.ToArray());

        return Result<FleetInvitePayload>.Success(payload);
    }

    private static string BuildInviteBody(
        string fleetName, FleetRole role, DateTimeOffset? fromTime, DateTimeOffset? toTime, string? note)
    {
        var body = $"You are invited to '{fleetName}' as {RoleLabel(role)}.";

        if (fromTime is { } from && toTime is { } to)
            body += $" Scheduled: {from:g}–{to:g}.";
        else if (fromTime is { } onlyFrom)
            body += $" Scheduled from {onlyFrom:g}.";
        else if (toTime is { } onlyTo)
            body += $" Scheduled until {onlyTo:g}.";

        if (!string.IsNullOrWhiteSpace(note))
            body += $" {note.Trim()}";

        return body;
    }

    private static string RoleLabel(FleetRole role) => role switch
    {
        FleetRole.FleetCommander => "Fleet Commander",
        FleetRole.WingCommander => "Wing Commander",
        FleetRole.SquadCommander => "Squad Commander",
        _ => "Squad Member"
    };
}
