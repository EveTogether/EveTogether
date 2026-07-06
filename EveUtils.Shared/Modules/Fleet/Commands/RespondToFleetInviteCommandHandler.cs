using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class RespondToFleetInviteCommandHandler(IFleetRepository repository)
    : ICommandHandler<RespondToFleetInviteCommand, Result<FleetInviteResponsePayload>>
{
    public async Task<Result<FleetInviteResponsePayload>> Handle(RespondToFleetInviteCommand command, CancellationToken cancellationToken = default)
    {
        var invite = await repository.GetInviteAsync(command.InviteId, cancellationToken);
        if (invite is null)
            return Result<FleetInviteResponsePayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "Invite not found.", "Fleet"));

        // Only the invitee may respond to their own invite.
        if (invite.InviteeCharacterId != command.ActingCharacterId)
            return Result<FleetInviteResponsePayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied, "Only the invitee can respond to this invite.", "Fleet"));

        if (invite.Status != FleetInviteStatus.Pending)
            return Result<FleetInviteResponsePayload>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Invite already responded to.", "Fleet"));

        // Accept needs a live target: if the fleet was disbanded/archived between invite and answer, joining
        // would yield an unusable member. Cancel the now-orphaned invite (so it stops re-delivering) and fail
        // with a clear message. Declining stays valid regardless — the invitee just clears it from the inbox.
        if (command.Accept)
        {
            var fleet = await repository.GetAsync(invite.FleetId, cancellationToken);
            if (fleet is null || fleet.State != FleetState.Active)
            {
                invite.Status = FleetInviteStatus.Cancelled;
                invite.RespondedAt = DateTimeOffset.UtcNow;
                await repository.UpdateInviteAsync(invite, cancellationToken);
                return Result<FleetInviteResponsePayload>.Failure(new ResultMessage(
                    MessageSeverity.Error, MessageCodes.ValidationFailed, "This fleet is no longer available.", "Fleet"));
            }

            // One active fleet per character + no joining a concluded fleet (2026-06-04). The invite stays Pending
            // so the invitee can resolve the conflict (leave/conclude their other fleet) and accept later.
            var joinable = await ActiveFleetMembershipGuard.EnsureJoinableAsync(repository, fleet, invite.InviteeCharacterId, cancellationToken);
            if (!joinable.IsSuccess)
                return Result<FleetInviteResponsePayload>.Failure(joinable.Messages.ToArray());
        }

        invite.Status = command.Accept ? FleetInviteStatus.Accepted : FleetInviteStatus.Denied;
        invite.RespondedAt = DateTimeOffset.UtcNow;
        await repository.UpdateInviteAsync(invite, cancellationToken);

        // Accepting adds the invitee to the roster (idempotent: the (FleetId, CharacterId) index is unique).
        if (command.Accept && !await repository.IsMemberAsync(invite.FleetId, invite.InviteeCharacterId, cancellationToken))
        {
            // An invite to a specific position keeps it; a positionless invite auto-places like any joiner — first
            // open squad, auto-creating the next squad/wing when full (2026-06-04), rather than landing unassigned.
            long wingId, squadId;
            if (invite.WingId is not null)
                (wingId, squadId) = (invite.WingId.Value, invite.SquadId ?? -1);
            else
                (wingId, squadId) = await FleetMemberPlacement.ResolveOrCreateSquadAsync(repository, invite.FleetId, cancellationToken);

            await repository.AddMemberAsync(new FleetMember
            {
                FleetId = invite.FleetId,
                CharacterId = invite.InviteeCharacterId,
                WingId = wingId,
                SquadId = squadId,
                Role = invite.Role,
                JoinTime = DateTimeOffset.UtcNow
            }, cancellationToken);
        }

        return Result<FleetInviteResponsePayload>.Success(new FleetInviteResponsePayload(
            invite.Id, invite.FleetId, invite.InviterCharacterId, invite.InviteeCharacterId, command.Accept));
    }
}
