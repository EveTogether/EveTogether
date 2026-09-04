using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet.Cleanup;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The client-only half of the automatic stop (ET-167). A client-only fleet is nobody's business but this machine's:
/// it never reaches a server, its members are the owner's own pilots, and no other client may stand it down. That
/// leaves only this client to notice — and this client has no periodic fleet task, so the reckoning happens at the
/// next start instead.
///
/// <para>Settling up on startup is not a weaker version of the server's five-minute pass; for a local fleet it is
/// the whole question. While the app runs, its own tick keeps the roster's <c>LastSeenAt</c> fresh, so a running
/// client is by definition a fleet that is still attended. The only way a local fleet goes quiet is the app being
/// closed, and the only moment that can be observed is the app being opened again — where the stored timestamps say
/// exactly how long the silence lasted. Closing the app therefore still does not stop a fleet: reopening it after
/// ninety seconds finds everything fresh and leaves the fleet running, which is the same promise a crash-restart
/// needs.</para>
///
/// Same <see cref="FleetAutoStopPolicy"/> and the same <c>StopFleetCommand</c> as the server, with the owner as the
/// acting character — one rule, on both hosts, with one set of thresholds.
/// </summary>
public sealed class LocalFleetAutoStopService(
    ClientFleetService fleets,
    ICharacterRegistry characters,
    IEsiAvailabilityState availability,
    IServiceScopeFactory scopeFactory,
    ILogger<LocalFleetAutoStopService> logger) : ISingletonService
{
    /// <summary>
    /// Stands down every client-only fleet of this machine's characters that the rule says should no longer be
    /// running. Returns what it stopped, so the startup path and a test can both see it happen.
    /// </summary>
    public async Task<IReadOnlyList<(long FleetId, FleetStopTrigger Trigger)>> ReconcileAsync(
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var options = FleetCleanupOptions.Default;
        // The ESI status poll has not run yet this early, so availability reads as usable and only the calendar can
        // speak. That is the right way round: the daily window is knowable without asking anyone, and an unplanned
        // outage that has not been observed cannot be the reason a fleet has been quiet since yesterday.
        var brakeEngaged = FleetAutoStopBrake.IsEngaged(
            now, availability.IsUsable, lastSeenUnavailableAt: null, options.ReconnectGrace);

        var owners = (await characters.GetAllAsync(cancellationToken))
            .Select(character => character.EsiCharacterId)
            .OfType<int>()
            .Distinct()
            .ToList();

        List<(long, FleetStopTrigger)> stopped = [];
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFleetRepository>();

        foreach (var ownerId in owners)
        foreach (FleetEntity fleet in await repository.ListByCreatorAsync(ownerId, cancellationToken))
        {
            if (!fleet.IsClientOnly)
                continue;

            var census = FleetPresenceCensus.Take(
                await repository.ListMembersAsync(fleet.Id, cancellationToken), now);
            var trigger = FleetAutoStopPolicy.Evaluate(
                fleet.State, fleet.Activation, census, fleet.LastActivityAt, now, brakeEngaged, options);
            if (trigger is not { } reason)
                continue;

            var result = await fleets.StopFleetAsync(fleet.Id, ownerId, reason, cancellationToken);
            if (!result.IsSuccess)
                continue;

            // Logged with the ground and the census it was decided on, the same way the server's sweep logs its own
            // (ET-167). A stop that turns out to have been wrong leaves a fleet that is simply standing by — which is
            // exactly what a stop the owner pressed looks like — so this line is the only way to find out afterwards
            // which rule fired and on what evidence.
            logger.LogInformation(
                "Local fleet auto-stop: '{FleetName}' ({FleetId}) stood down by {Trigger}; roster {MemberCount}, present {PresentCount}, ever heard {EverHeardCount}.",
                fleet.Name, fleet.Id, reason, census.MemberCount, census.PresentCount, census.EverHeardCount);
            stopped.Add((fleet.Id, reason));
        }

        return stopped;
    }
}
