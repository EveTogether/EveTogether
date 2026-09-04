using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;

namespace EveUtils.Shared.Modules.Fleet.Cleanup;

/// <summary>
/// The pure decision behind the automatic stop (ET-167): should this fleet go back to standing by, and on which of
/// the two grounds. No I/O — the runner supplies the census and applies the result — so the rule is unit-testable
/// on its own and is the same rule on the server and in the client.
///
/// Deliberately not folded into <see cref="FleetCleanupPolicy"/>. That one answers a different question (which
/// fleets may be archived or deleted) and answers it with "never touch a fleet that is not Concluded"; this one only
/// ever fires on a fleet that <i>is</i> in-game Active, and its outcome is a reversible phase change rather than a
/// soft-delete. Two rules that both look at an Active fleet and must not learn each other's exceptions.
///
/// The two grounds are not symmetrical, and that asymmetry is the point:
/// <list type="bullet">
/// <item><b>Everyone left</b> — the roster is empty. True through any outage: whoever left stayed gone. Held only by
/// the inactivity grace, so that starting a fleet and then inviting into it is not a race against the sweep.</item>
/// <item><b>Everyone is offline</b> — nobody has been heard from for <c>FleetMemberPresence.SilentAfter</c>. This is
/// the one a restart fakes for the whole server at once, so it answers to <see cref="FleetAutoStopBrake"/>.</item>
/// </list>
/// </summary>
public static class FleetAutoStopPolicy
{
    /// <summary>
    /// The reason to stop this fleet, or null to leave it running.
    /// </summary>
    /// <param name="state">Soft-delete lifecycle; an archived fleet is nobody's business here.</param>
    /// <param name="activation">Only an in-game-Active fleet can be stopped — Forming is already the destination
    /// and Concluded is terminal.</param>
    /// <param name="census">The roster as of <paramref name="now"/>.</param>
    /// <param name="lastActivityAt">The fleet's own clock, bumped by member events and by live traffic.</param>
    /// <param name="brakeEngaged"><see cref="FleetAutoStopBrake.IsEngaged"/> — withholds the offline ground only.</param>
    public static FleetStopTrigger? Evaluate(
        FleetState state,
        FleetActivation activation,
        FleetPresenceCensus census,
        DateTimeOffset lastActivityAt,
        DateTimeOffset now,
        bool brakeEngaged,
        FleetCleanupOptions options)
    {
        if (state != FleetState.Active || activation != FleetActivation.Active)
            return null;

        if (census.MemberCount == 0)
            // The grace is not a brake against downtime — an empty roster is an empty roster at 11:00 too — but
            // against the FC who starts a fleet and invites into it afterwards. Between the start and the first
            // accepted invite the roster is legitimately empty, and without this the sweep would stand the fleet
            // back down while its invitations were still out.
            return now - lastActivityAt >= options.InactivityGrace ? FleetStopTrigger.RosterEmpty : null;

        if (census.PresentCount > 0)
            return null;

        // Nobody has ever published into this fleet, so there is no silence to read — only members who have not
        // arrived yet, or a roster of externals who never could. ET-70's rule, and it is what keeps a fleet started
        // ahead of its pilots from standing itself down ninety seconds later.
        if (census.EverHeardCount == 0)
            return null;

        return brakeEngaged ? null : FleetStopTrigger.AllMembersOffline;
    }
}
