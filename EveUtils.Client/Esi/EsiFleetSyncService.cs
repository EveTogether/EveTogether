using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Client.Transport;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Client.Esi;

/// <summary>
/// Boss-side roster mirror for the in-game fleet coupling. For each linked fleet whose in-game boss is one
/// of our characters — both client-only fleets (local repository) and server fleets (the server only stores + relays
/// the EsiFleetId; all ESI work is client-side) — polls the live roster (<c>GET /fleets/{id}/members/</c>, 5s ESI cache
/// via the metered pipeline) and diffs it against our planned doctrine roster, surfacing who joined, who's still missing
/// and who is in-game but off-plan. On a change it publishes a local <c>fleet.changed</c>
/// (<see cref="FleetChangeKind.RosterChanged"/>) so open roster windows refresh. When our character is not the boss the
/// read fails and the mirror is skipped — that case is covered by member self-report. Mirrors
/// <see cref="CharacterInfoRefreshService"/>'s periodic shape.
/// </summary>
public sealed class EsiFleetSyncService(
    IEsiFleetClient fleetClient,
    IFleetRepository repository,
    ICharacterRegistry registry,
    IClientSessionStore sessions,
    IFleetTransportClient transport,
    IEventBus eventBus,
    IFleetRosterChangeNotifier changeNotifier,
    IEsiAvailabilityState availability,
    ILogger<EsiFleetSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5); // the members/wings ESI cache TTL
    private const int DecoupleAfterMissingPolls = 2; // tolerate a brief blip (~10s); decouple once the in-game fleet is clearly gone
    private const int DecoupleAfterPersistentFailures = 60; // ~5 min of unbroken non-404 failure: a gone fleet that ESI answers with 500 instead of 404 would never trip the fast NotFound path, so it is decoupled here
    private readonly Dictionary<string, FleetRosterDiff> _lastDiffs = new();
    private readonly Dictionary<string, int> _notFoundStreak = new(); // consecutive "in-game fleet not found" polls per fleet
    private readonly Dictionary<string, int> _failureStreak = new(); // consecutive failed polls of any kind per fleet

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await SyncLinkedFleetsAsync(stoppingToken);
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

    private async Task SyncLinkedFleetsAsync(CancellationToken cancellationToken)
    {
        // ESI is down — skip the cycle rather than poll a dead API. The failure/missing streaks are not touched, so a
        // fleet is never wrongly decoupled just because ESI was briefly unreachable (recovery resumes the mirror).
        if (!availability.IsUsable)
        {
            logger.LogDebug("ESI unavailable — skipping fleet roster sync this cycle.");
            return;
        }

        try
        {
            await SyncLocalFleetsAsync(cancellationToken);
            await SyncServerFleetsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ESI fleet roster sync failed.");
        }
    }

    private async Task SyncLocalFleetsAsync(CancellationToken cancellationToken)
    {
        var seen = new HashSet<long>();
        foreach (var character in await registry.GetAllAsync(cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested || character.EsiCharacterId is not { } characterId)
                continue;
            foreach (var fleet in await repository.ListByCreatorAsync(characterId, cancellationToken))
                if (fleet.EsiSyncState == EsiFleetSyncState.Linked && seen.Add(fleet.Id))
                    await SyncFleetAsync(fleet, cancellationToken);
        }
    }

    private async Task SyncServerFleetsAsync(CancellationToken cancellationToken)
    {
        foreach (var server in await sessions.ListServersAsync(cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            foreach (var session in await sessions.LoadAllAsync(server, cancellationToken))
                await SyncServerFleetsForCharacterAsync(server, session.CharacterId, cancellationToken);
        }
    }

    /// <summary>
    /// Boss-side roster mirror for the SERVER fleets this character owns. For each coupled fleet whose in-game boss is
    /// this character, polls the live roster client-side (boss token) and diffs it against the server-held plan,
    /// broadcasting RosterChanged on a change. Public for testing. The non-boss case is covered by member self-report.
    /// A definitive NotFound (the in-game fleet dissolved/re-formed) uncouples the server fleet via the uncouple RPC
    /// the server keeps no ESI logic, but clearing the stored EsiFleetId is its storage role, so no client
    /// keeps polling ESI for a dead fleet.
    /// </summary>
    public async Task SyncServerFleetsForCharacterAsync(string serverAddress, int characterId, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var fleet in await transport.ListMyFleetsAsync(serverAddress, characterId, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                if (fleet.EsiFleetId is not { } esiFleetId || fleet.EsiFleetBossId != characterId)
                    continue; // not coupled, or we are not the in-game boss (member self-report covers the non-boss case)

                var planned = (await transport.ListMembersAsync(serverAddress, fleet.Id, characterId, cancellationToken))
                    .Where(member => !member.IsExternal).Select(member => member.CharacterId).ToList();

                await MirrorRosterAsync($"{serverAddress}:{fleet.Id}", fleet.Id, esiFleetId, characterId, planned,
                    () => UncoupleServerFleetAsync(serverAddress, fleet.Id, characterId, cancellationToken), cancellationToken);
            }
        }
        catch (FleetTransportException ex)
        {
            // The server is unreachable this cycle (down, or the connection dropped) — skip it quietly and retry next
            // tick rather than logging an error every 5s; the local fleets + other servers still sync.
            logger.LogDebug(ex, "Skipped ESI server-fleet sync for {Server} — unreachable this cycle.", serverAddress);
        }
    }

    /// <summary>
    /// Mirrors one linked client-only fleet's live roster and diffs it against the plan; on a change broadcasts
    /// RosterChanged. Returns the diff, or <c>null</c> when the fleet is not linked, our character is not the boss, or
    /// the read failed. A definitive <c>NotFound</c> (the in-game fleet dissolved/re-formed, or we are no longer its
    /// boss) unlinks the fleet so the poller stops hammering a dead fleet — transient failures keep the link.
    /// </summary>
    public Task<FleetRosterDiff?> SyncFleetAsync(FleetEntity fleet, CancellationToken cancellationToken = default)
    {
        if (fleet.EsiSyncState != EsiFleetSyncState.Linked
            || fleet.EsiFleetId is not { } esiFleetId
            || fleet.EsiFleetBossId is not { } bossCharacterId)
            return Task.FromResult<FleetRosterDiff?>(null);

        return MirrorLocalAsync(fleet, esiFleetId, bossCharacterId, cancellationToken);
    }

    private async Task<FleetRosterDiff?> MirrorLocalAsync(FleetEntity fleet, long esiFleetId, int bossCharacterId, CancellationToken cancellationToken)
    {
        var planned = (await repository.ListMembersAsync(fleet.Id, cancellationToken))
            .Where(member => !member.IsExternal).Select(member => member.CharacterId).ToList();

        return await MirrorRosterAsync($"local:{fleet.Id}", fleet.Id, esiFleetId, bossCharacterId, planned,
            () => UnlinkAsync(fleet, cancellationToken), cancellationToken);
    }

    // The shared mirror step both paths run: poll the live ESI roster (boss token), diff it against the plan, and on a
    // change notify + broadcast RosterChanged keyed by the (open-window) fleet id. A definitive NotFound runs the
    // path's own cleanup (<paramref name="onNotFound"/>); transient failures leave the link for the next tick.
    private async Task<FleetRosterDiff?> MirrorRosterAsync(string dedupKey, long broadcastFleetId, long esiFleetId,
        int bossCharacterId, IReadOnlyList<int> plannedCharacterIds, Func<Task> onNotFound, CancellationToken cancellationToken)
    {
        var members = await fleetClient.GetMembersAsync(esiFleetId, bossCharacterId, cancellationToken);
        if (!members.IsSuccess || members.Value is null)
        {
            // A NotFound is the clear "in-game fleet gone / no longer boss" signal — decouple fast, after only a
            // couple of consecutive misses (a brief re-form or boss-change blip is tolerated).
            if (members.Error?.Kind == EsiErrorKind.NotFound)
            {
                var streak = _notFoundStreak.GetValueOrDefault(dedupKey) + 1;
                _notFoundStreak[dedupKey] = streak;
                if (streak >= DecoupleAfterMissingPolls)
                {
                    ClearStreaks(dedupKey);
                    await onNotFound();
                }
                return null;
            }

            // A server-side failure (5xx / gateway timeout) is normally transient, so we keep the link. But a fleet
            // that is truly gone yet answered with a persistent non-404 (e.g. ESI returns 500 instead of 404) would
            // never trip the fast NotFound path and would be polled forever, so decouple after a long unbroken run —
            // a transient blip is broken by the interleaved successful polls that reset the streak. Local failures
            // (auth/scope/rate-limit/offline) say nothing about whether the fleet still exists, so they neither
            // count toward the streak nor reset it — only a never-recovering server-side run reaches the threshold.
            if (IsServerSideFailure(members.Error?.Kind))
            {
                var failures = _failureStreak.GetValueOrDefault(dedupKey) + 1;
                _failureStreak[dedupKey] = failures;
                if (failures >= DecoupleAfterPersistentFailures)
                {
                    ClearStreaks(dedupKey);
                    await onNotFound();
                }
            }
            return null;
        }

        ClearStreaks(dedupKey); // a successful poll clears both the missing and the persistent-failure streaks

        var diff = FleetRosterDiffer.Diff(plannedCharacterIds, members.Value.Select(member => member.CharacterId));

        _lastDiffs.TryGetValue(dedupKey, out var previous);
        if (previous is not null && RosterDiffEquals(previous, diff))
            return diff; // unchanged → no spurious refresh

        _lastDiffs[dedupKey] = diff;
        await changeNotifier.NotifyAsync(previous, diff, cancellationToken);
        await eventBus.PublishAsync(
            new FleetChangedEvent(new FleetChangePayload(broadcastFleetId, FleetChangeKind.RosterChanged)),
            EventTarget.Local, cancellationToken);
        return diff;
    }

    // The coupling is permanently broken — drop it so the poller stops hammering a dead fleet, and clear the stale
    // in-game ids (incl. wings/squads) so a fresh couple + structure-push rebuilds cleanly instead of skipping
    // already-"linked" units that no longer exist in-game.
    private async Task UnlinkAsync(FleetEntity fleet, CancellationToken cancellationToken)
    {
        fleet.EsiSyncState = EsiFleetSyncState.NotLinked;
        fleet.EsiFleetId = null;
        fleet.EsiFleetBossId = null;
        await repository.UpdateAsync(fleet, cancellationToken);

        foreach (var wing in await repository.ListWingsAsync(fleet.Id, cancellationToken))
        {
            if (wing.EsiWingId is not null)
            {
                wing.EsiWingId = null;
                await repository.UpdateWingAsync(wing, cancellationToken);
            }
            foreach (var squad in await repository.ListSquadsAsync(wing.Id, cancellationToken))
            {
                if (squad.EsiSquadId is not null)
                {
                    squad.EsiSquadId = null;
                    await repository.UpdateSquadAsync(squad, cancellationToken);
                }
            }
        }

        _lastDiffs.Remove($"local:{fleet.Id}");
        await eventBus.PublishAsync(
            new FleetChangedEvent(new FleetChangePayload(fleet.Id, FleetChangeKind.RosterChanged)),
            EventTarget.Local, cancellationToken);
        logger.LogInformation("Unlinked fleet {FleetId} from ESI — the in-game fleet dissolved or its boss changed.", fleet.Id);
    }

    // The server-fleet counterpart of UnlinkAsync: the in-game fleet is gone, so ask the server to clear the
    // stored EsiFleetId (its storage role — no ESI call) instead of leaving a dead link other clients would still poll.
    // On success the next ListMyFleetsAsync returns it uncoupled, so the poll stops naturally. If the server refuses or
    // is unreachable, keep the link — the missing-streak re-attempts a few polls later (self-healing).
    private async Task UncoupleServerFleetAsync(string serverAddress, long fleetId, int bossCharacterId, CancellationToken cancellationToken)
    {
        var (ok, message) = await transport.UncoupleFleetFromEsiAsync(serverAddress, fleetId, bossCharacterId, cancellationToken);
        if (!ok)
        {
            logger.LogWarning("Could not uncouple server fleet {FleetId} on {Server} from ESI: {Message}", fleetId, serverAddress, message);
            return;
        }

        _lastDiffs.Remove($"{serverAddress}:{fleetId}");
        await eventBus.PublishAsync(
            new FleetChangedEvent(new FleetChangePayload(fleetId, FleetChangeKind.RosterChanged)),
            EventTarget.Local, cancellationToken);
        logger.LogInformation("Uncoupled server fleet {FleetId} on {Server} from ESI — the in-game fleet is gone.", fleetId, serverAddress);
    }

    // Both the fast NotFound streak and the slow persistent-failure streak are per-fleet and cleared together: a
    // successful poll or a decouple ends both runs.
    private void ClearStreaks(string dedupKey)
    {
        _notFoundStreak.Remove(dedupKey);
        _failureStreak.Remove(dedupKey);
    }

    // Only a failure where ESI itself (or the gateway in front of it) failed counts toward the persistent-failure
    // decouple: a gone fleet that answers 500/504 instead of 404. Local failures — auth/scope/rate-limit/offline —
    // tell us nothing about whether the in-game fleet still exists, so they must never drive an uncouple.
    private static bool IsServerSideFailure(EsiErrorKind? kind) =>
        kind is EsiErrorKind.ServerError or EsiErrorKind.Timeout;

    private static bool RosterDiffEquals(FleetRosterDiff a, FleetRosterDiff b) =>
        a.Present.SequenceEqual(b.Present) && a.Missing.SequenceEqual(b.Missing) && a.External.SequenceEqual(b.External);
}
