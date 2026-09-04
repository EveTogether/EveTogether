using System.Linq;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

internal sealed class SwitchToFleetCommandHandler(IFleetRepository repository, IDispatcher dispatcher)
    : ICommandHandler<SwitchToFleetCommand, Result<IReadOnlyList<long>>>
{
    public async Task<Result<IReadOnlyList<long>>> Handle(SwitchToFleetCommand command, CancellationToken cancellationToken = default)
    {
        var fleet = await repository.GetAsync(command.FleetId, cancellationToken);
        if (fleet is null || fleet.State != FleetState.Active)
            return Result<IReadOnlyList<long>>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "This fleet is no longer available.", "Fleet"));

        // A request stands for as long as the fleet runs and no longer: switching into a fleet that has stopped or
        // concluded would leave the member linked to nothing at all.
        if (fleet.Activation != FleetActivation.Active)
            return Result<IReadOnlyList<long>>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed,
                $"'{fleet.Name}' is not running any more — there is nothing to switch to.", "Fleet"));

        // ── Everything that can refuse, refused before anything moves ──────────────────────────────────────────
        // Both halves are checked up front, because a switch that leaves the other fleet and then cannot couple
        // here has made the member worse off than doing nothing: out of one fleet and into none.

        var leaving = new List<FleetMember>();
        foreach (var membership in await repository.ListActiveMembershipsAsync(command.ActingCharacterId, cancellationToken))
        {
            if (membership.FleetId == fleet.Id)
                continue;

            var other = await repository.GetAsync(membership.FleetId, cancellationToken);
            if (other is null)
                continue;

            // A commander cannot walk out of their own fleet — the same rule RemoveFleetMemberCommandHandler
            // enforces, and for the same reason: a fleet always has an owner. Stopping it is the way out.
            if (other.CreatorCharacterId == command.ActingCharacterId)
                return Result<IReadOnlyList<long>>.Failure(new ResultMessage(
                    MessageSeverity.Error, MessageCodes.ValidationFailed,
                    $"You command '{other.Name}'. Stop or conclude it before switching, or hand it over first.", "Fleet"));

            var seat = (await repository.ListMembersAsync(other.Id, cancellationToken))
                .FirstOrDefault(m => m.CharacterId == command.ActingCharacterId);
            if (seat is not null)
                leaving.Add(seat);
        }

        var alreadyOnRoster = await repository.IsMemberAsync(fleet.Id, command.ActingCharacterId, cancellationToken);
        var invite = alreadyOnRoster
            ? null
            : (await repository.ListPendingInvitesForInviteeAsync(command.ActingCharacterId, cancellationToken))
                .FirstOrDefault(i => i.FleetId == fleet.Id);

        // How the member gets a seat here, in the order that keeps a door open where JOIN closes one. A member who
        // is already on the roster needs nothing — which is the whole of "hooking up later" for an invite-only
        // fleet, where JoinFleetCommandHandler would refuse on visibility alone.
        if (!alreadyOnRoster && invite is null && fleet.Visibility != FleetVisibility.Public)
            return Result<IReadOnlyList<long>>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.PermissionDenied,
                $"'{fleet.Name}' is invite-only and you are not on its roster — ask its commander for an invite.", "Fleet"));

        // ── Step 1: leave. The other fleet keeps running for everyone else; only this character comes free. ────
        var now = DateTimeOffset.UtcNow;
        foreach (var seat in leaving)
        {
            await repository.RemoveMemberAsync(seat.Id, cancellationToken);
            await repository.TouchActivityAsync(seat.FleetId, now, cancellationToken);
        }

        // ── Step 2: couple here. With every other active membership gone the entry-guard now passes, which is the
        // very refusal this command exists to walk the member through rather than report at them.
        if (!alreadyOnRoster)
        {
            var joinable = await ActiveFleetMembershipGuard.EnsureJoinableAsync(
                repository, fleet, command.ActingCharacterId, cancellationToken);
            if (!joinable.IsSuccess)
                return Result<IReadOnlyList<long>>.Failure(joinable.Messages.ToArray());

            long wingId, squadId;
            var role = FleetRole.SquadMember;
            if (invite is not null)
            {
                invite.Status = FleetInviteStatus.Accepted;
                invite.RespondedAt = now;
                await repository.UpdateInviteAsync(invite, cancellationToken);
                role = invite.Role;
                (wingId, squadId) = invite.WingId is not null
                    ? (invite.WingId.Value, invite.SquadId ?? -1)
                    : await FleetMemberPlacement.ResolveOrCreateSquadAsync(repository, fleet.Id, cancellationToken);
            }
            else
            {
                (wingId, squadId) = await FleetMemberPlacement.ResolveOrCreateSquadAsync(repository, fleet.Id, cancellationToken);
            }

            await repository.AddMemberAsync(new FleetMember
            {
                FleetId = fleet.Id,
                CharacterId = command.ActingCharacterId,
                Role = role,
                WingId = wingId,
                SquadId = squadId,
                JoinTime = now
            }, cancellationToken);
        }

        await repository.TouchActivityAsync(fleet.Id, now, cancellationToken);

        // The commander asked; they should hear the answer without having to watch the roster. Plain mail: this is
        // a notification, not something to accept or decline.
        if (fleet.CreatorCharacterId != command.ActingCharacterId)
        {
            var notify = await dispatcher.Send(new EnqueueMessageCommand(
                fleet.CreatorCharacterId, command.ActingCharacterId, MessageKind.Mail,
                $"Switched over: {fleet.Name}",
                $"A member switched to {fleet.Name} and is linked from now on.",
                null, fleet.Id), cancellationToken);
            if (!notify.IsSuccess)
                return Result<IReadOnlyList<long>>.Failure(notify.Messages.ToArray());
        }

        return Result<IReadOnlyList<long>>.Success(leaving.Select(seat => seat.FleetId).ToList());
    }
}
