using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Shared entry-guard for becoming a roster member of a fleet (2026-06-04). Enforces EVE parity: a character may
/// be a broadcasting member of at most one <see cref="FleetActivation.Active"/> fleet at a time, and a
/// <see cref="FleetActivation.Concluded"/> fleet can no longer be joined. Signing up in advance to several
/// <see cref="FleetActivation.Forming"/> fleets stays allowed — only active simultaneity is blocked.
/// The remaining edge (signed up to a Forming fleet that is then started while already active elsewhere) is
/// resolved at broadcast time by <c>FleetBroadcastResolver</c>'s activated-first tiebreak.
/// </summary>
internal static class ActiveFleetMembershipGuard
{
    public static async Task<Result> EnsureJoinableAsync(
        IFleetRepository repository, FleetEntity fleet, int characterId, CancellationToken cancellationToken)
    {
        if (fleet.Activation == FleetActivation.Concluded)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.ValidationFailed, "Fleet has concluded and can no longer be joined.", "Fleet"));

        // Joining an already-started (Active) fleet while already in a *different* active fleet → block. Joining a
        // Forming fleet stays allowed even when active elsewhere (advance sign-up): the eventual conflict — that
        // Forming fleet being started later — is handled by the broadcast tiebreak, which keeps the character coupled
        // to the fleet they were activated in first. Membership of this same fleet (idempotent re-join) is fine.
        if (fleet.Activation == FleetActivation.Active)
        {
            var actives = await repository.ListActiveMembershipsAsync(characterId, cancellationToken);
            var other = actives.FirstOrDefault(m => m.FleetId != fleet.Id);
            if (other is not null)
                return Result.Failure(new ResultMessage(
                    MessageSeverity.Error, MessageCodes.ValidationFailed,
                    $"Character is already in active fleet '{other.FleetName}'. Leave or conclude it before joining another.", "Fleet"));
        }

        return Result.Success();
    }
}
