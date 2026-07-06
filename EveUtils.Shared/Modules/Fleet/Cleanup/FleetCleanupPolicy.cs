using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Cleanup;

/// <summary>
/// The pure cleanup decision for one fleet: only a <see cref="FleetActivation.Concluded"/> fleet is
/// auto-archived — Forming and in-game-Active fleets are never swept, so a fleet planned days ahead survives for
/// members to sign up and fit. A concluded fleet with no active participant and no member event for the
/// grace-period is archived (soft-delete); a concluded fleet past its planned end-time skips the grace; an
/// Archived fleet is hard-deleted once it has been archived longer than the keep-window. No I/O — the runner
/// supplies the inputs and applies the result, so the rule is unit-testable on its own.
/// </summary>
public static class FleetCleanupPolicy
{
    public static FleetCleanupAction Evaluate(
        FleetState state,
        FleetActivation activation,
        DateTimeOffset? toTime,
        DateTimeOffset lastActivityAt,
        bool hasActiveParticipants,
        DateTimeOffset now,
        FleetCleanupOptions options)
    {
        switch (state)
        {
            case FleetState.Active:
                // Only a concluded op is auto-archived: a Forming fleet (incl. one scheduled days ahead) and
                // a live in-game-Active fleet are kept until the owner disbands or an admin removes them — auto-archiving
                // them would make a planned fleet vanish before anyone flies it.
                if (activation != FleetActivation.Concluded)
                    return FleetCleanupAction.None;

                // An active participant keeps a fleet alive regardless of timestamps.
                if (hasActiveParticipants)
                    return FleetCleanupAction.None;

                // End-time accelerates: a fleet past its planned end is cleaned up promptly, no grace.
                var grace = toTime is { } end && end <= now ? TimeSpan.Zero : options.InactivityGrace;
                return now - lastActivityAt >= grace ? FleetCleanupAction.Archive : FleetCleanupAction.None;

            case FleetState.Archived:
                // LastActivityAt is stamped to "now" at archive time, so it doubles as the archived-at clock.
                return now - lastActivityAt >= options.HardDeleteAfter ? FleetCleanupAction.Delete : FleetCleanupAction.None;

            default:
                return FleetCleanupAction.None;
        }
    }
}
