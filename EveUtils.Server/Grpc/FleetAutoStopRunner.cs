using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Cleanup;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Grpc;

/// <summary>
/// One automatic-stop sweep over the started fleets (ET-167): a fleet whose roster has emptied, or whose members
/// have all gone quiet, goes back to standing by. Pulled out of the background service the way
/// <see cref="FleetCleanupRunner"/> was, so a headless check can run a deterministic sweep against a supplied "now".
/// The decision is the pure <see cref="FleetAutoStopPolicy"/>; this only loads and dispatches — the stop's own signal
/// reaches the fleet's watchers through <see cref="FleetChangeAnnouncer"/>, owner included, like a pressed STOP.
///
/// <para><b>This is the first path on which the server changes a fleet's phase by itself, so the shape of it matters
/// more than its length.</b> It opens no new door: it sends the ordinary <see cref="StopFleetCommand"/> with the
/// fleet's own <c>CreatorCharacterId</c> as the acting character, which is what the owner's decision that "the fleet
/// owner is the one who pressed it" amounts to in code — the creator-only guard in the handler is satisfied on its
/// own terms rather than bypassed. What is genuinely new is that the acting character no longer comes from a
/// validated session; the fleet row supplies it. Three things keep that safe: the id is never taken from a request,
/// the only reachable outcome is the reversible <c>Active → Forming</c> (never conclude, archive or delete), and the
/// stop carries a <see cref="FleetStopTrigger"/> so nothing downstream can mistake it for a pressed button.</para>
///
/// <para>Presence is read off <c>FleetMember.LastSeenAt</c>, not off <see cref="ConnectedClients"/>: a live socket
/// answers "is this machine attached to me", and the question here is "is this pilot still here", which
/// <c>FleetMemberPresence.SilentAfter</c> already defines and which survives a reconnect. That timestamp had only
/// ever been read client-side; reading it server-side is the work this ticket adds.</para>
/// </summary>
public sealed class FleetAutoStopRunner(
    IFleetReader repository,
    IDispatcher dispatcher,
    ILogger<FleetAutoStopRunner> logger)
{
    public async Task<SweepResult> SweepAsync(
        DateTimeOffset now,
        FleetCleanupOptions options,
        bool brakeEngaged,
        CancellationToken cancellationToken = default)
    {
        var rosterEmpty = 0;
        var allOffline = 0;

        foreach (var fleet in await repository.ListByStateAsync(FleetState.Active, cancellationToken))
        {
            if (fleet.Activation != FleetActivation.Active)
                continue;

            var members = await repository.ListMembersAsync(fleet.Id, cancellationToken);
            var census = FleetPresenceCensus.Take(members, now);
            var trigger = FleetAutoStopPolicy.Evaluate(
                fleet.State, fleet.Activation, census, fleet.LastActivityAt, now, brakeEngaged, options);
            if (trigger is not { } reason)
                continue;

            var stopped = await dispatcher.Send(
                new StopFleetCommand(fleet.Id, fleet.CreatorCharacterId, reason), cancellationToken);
            // A fleet that refuses to stop is not a reason to abandon the sweep: the next fleet's members are
            // waiting on their own answer, and this one will be reconsidered in five minutes.
            if (!stopped.IsSuccess)
                continue;

            // Logged per fleet, with the ground and the census it was decided on. A stop that turns out to have been
            // wrong leaves nothing else behind — the fleet is simply standing by, which is also what it looks like
            // when the FC stopped it — so this line is the only way to find out afterwards which rule fired and on
            // what evidence.
            logger.LogInformation(
                "Fleet auto-stop: '{FleetName}' ({FleetId}) stood down by {Trigger}; roster {MemberCount}, present {PresentCount}, ever heard {EverHeardCount}.",
                fleet.Name, fleet.Id, reason, census.MemberCount, census.PresentCount, census.EverHeardCount);

            if (reason == FleetStopTrigger.RosterEmpty)
                rosterEmpty++;
            else
                allOffline++;
        }

        return new SweepResult(rosterEmpty, allOffline);
    }

    public readonly record struct SweepResult(int RosterEmpty, int AllOffline)
    {
        public int Total => RosterEmpty + AllOffline;
    }
}
