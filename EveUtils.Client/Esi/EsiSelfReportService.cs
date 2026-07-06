using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Transport;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Esi;

/// <summary>
/// Member self-report driver: for each coupled server-fleet this client is a NON-boss member of, polls the
/// character's own <c>GET /characters/{id}/fleet/</c> (60s ESI cache → gentle) and reports to that server whether the
/// pilot is in the coupled in-game fleet — so the roster reflects who actually joined even when nobody reads it as boss
/// (the boss case is covered by <see cref="EsiFleetSyncService"/>). Reports only when the presence changed since the
/// last cycle (the server command is idempotent anyway). Mirrors <see cref="EsiFleetSyncService"/>'s periodic shape.
/// </summary>
public sealed class EsiSelfReportService(
    IClientSessionStore sessions,
    IFleetTransportClient transport,
    IEsiFleetClient fleetClient,
    IEsiAvailabilityState availability,
    ILogger<EsiSelfReportService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60); // the /characters/{id}/fleet/ ESI cache TTL
    private readonly Dictionary<(string Server, long MemberId), bool> _lastReported = new();
    private readonly Dictionary<(string Server, long FleetId, int CharacterId), long> _memberIds = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ReportAllAsync(stoppingToken);
            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ReportAllAsync(CancellationToken cancellationToken)
    {
        // ESI is down — the char-fleet read would be withheld anyway, so skip the cycle (presence is left as-is and
        // resumes once ESI recovers).
        if (!availability.IsUsable)
        {
            logger.LogDebug("ESI unavailable — skipping member self-report this cycle.");
            return;
        }

        try
        {
            foreach (var server in await sessions.ListServersAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                foreach (var session in await sessions.LoadAllAsync(server, cancellationToken))
                {
                    try
                    {
                        await ReportForCharacterAsync(server, session.CharacterId, cancellationToken);
                    }
                    catch (FleetTransportException ex)
                    {
                        // Server unreachable this cycle — skip it quietly (Debug) instead of failing the whole cycle.
                        logger.LogDebug(ex, "Skipped ESI self-report for {Server} — unreachable this cycle.", server);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ESI member self-report cycle failed.");
        }
    }

    /// <summary>
    /// Reports one character's in-game presence to each coupled server-fleet it is a non-boss member of. Public for
    /// testing. A NotFound from the char-fleet read means "in no fleet"; a transient failure skips the character this
    /// cycle so a timeout never wrongly retracts presence.
    /// </summary>
    public async Task ReportForCharacterAsync(string server, int characterId, CancellationToken cancellationToken = default)
    {
        var myFleets = await transport.ListMyFleetsAsync(server, characterId, cancellationToken);

        // Gate the /characters/{id}/fleet/ ESI read behind "am I a non-boss member of any coupled fleet?". Without one
        // there is nothing to report into, so the read is pure waste — and would otherwise keep 404-polling that endpoint
        // forever after an uncouple. (Fleets we boss are mirrored by EsiFleetSyncService, so they don't count here.)
        if (!myFleets.Any(fleet => fleet.EsiFleetId is not null && fleet.EsiFleetBossId != characterId))
            return;

        var charFleet = await fleetClient.GetCharacterFleetAsync(characterId, cancellationToken);
        if (!charFleet.IsSuccess && charFleet.Error?.Kind != EsiErrorKind.NotFound)
            return; // transient — leave presence as-is, retry next cycle
        var inGameFleetId = charFleet.Value?.FleetId;

        foreach (var fleet in myFleets)
        {
            if (fleet.EsiFleetId is not { } esiFleetId || fleet.EsiFleetBossId == characterId)
                continue; // not coupled, or we are the boss (the boss-side mirror covers it)

            var memberId = await ResolveMemberIdAsync(server, fleet.Id, characterId, cancellationToken);
            if (memberId is not { } member)
                continue; // not a roster member of this fleet

            var inFleet = inGameFleetId == esiFleetId;
            if (_lastReported.TryGetValue((server, member), out var previous) && previous == inFleet)
                continue; // unchanged since last cycle — skip the round-trip (the report is idempotent anyway)

            _lastReported[(server, member)] = inFleet;
            await transport.ReportMemberInGameFleetAsync(server, member, inFleet, characterId, cancellationToken);
        }
    }

    // The roster member id for this character is stable, so cache it to avoid a ListMembers call every cycle.
    private async Task<long?> ResolveMemberIdAsync(string server, long fleetId, int characterId, CancellationToken cancellationToken)
    {
        var key = (server, fleetId, characterId);
        if (_memberIds.TryGetValue(key, out var cached))
            return cached;

        var roster = await transport.ListMembersAsync(server, fleetId, characterId, cancellationToken);
        var me = roster.FirstOrDefault(member => member.CharacterId == characterId);
        if (me is null)
            return null;

        _memberIds[key] = me.Id;
        return me.Id;
    }
}
