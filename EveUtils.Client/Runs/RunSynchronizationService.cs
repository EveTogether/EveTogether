using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Client.Runs;

public sealed class RunSynchronizationService(
    IDbContextFactory<ClientDbContext> contextFactory,
    IServerRunSyncClient client,
    RunSynchronizationApplier applier,
    IEventBus eventBus,
    IMetricShareSettings shares) : IScopedService
{
    public Task<(bool Accepted, string Message)> SynchronizeAsync(string serverAddress, long characterId,
        CancellationToken cancellationToken = default) =>
        _SynchronizeAsync(serverAddress, characterId, onlyGroupCodes: null, pushPending: true, cancellationToken);

    /// <summary>The same exchange narrowed to <paramref name="groupCodes"/> (ET-245): only this character's runs queued
    /// for this server in those groups are pushed, and only those groups are pulled. What the automatic publish and a
    /// group mate's notice use, so neither ever carries along a run the pilot queued for another activity, nor reads
    /// back the whole history on every save. <paramref name="pushPending"/> false only pulls.</summary>
    public Task<(bool Accepted, string Message)> SynchronizeGroupsAsync(string serverAddress, long characterId,
        IReadOnlyCollection<string> groupCodes, bool pushPending, CancellationToken cancellationToken = default) =>
        groupCodes.Count == 0
            ? Task.FromResult((true, "Nothing to synchronize."))
            : _SynchronizeAsync(serverAddress, characterId, [.. groupCodes.Distinct()], pushPending, cancellationToken);

    private async Task<(bool Accepted, string Message)> _SynchronizeAsync(string serverAddress, long characterId,
        string[]? onlyGroupCodes, bool pushPending, CancellationToken cancellationToken)
    {
        IReadOnlyList<Run> localRuns = await _LoadGroupRunsAsync(onlyGroupCodes, cancellationToken);
        string[] groupCodes = localRuns.Select(run => run.GroupCode).Where(groupCode => groupCode is not null).Cast<string>().Distinct().ToArray();
        DateTime waterline = localRuns.Where(run => run.LastPushedAtUtc.HasValue)
            .Select(run => run.LastPushedAtUtc.GetValueOrDefault()).DefaultIfEmpty(DateTime.UnixEpoch).Min();

        IReadOnlyList<Run> pendingRuns = pushPending
            ? await _LoadPendingAsync(serverAddress, characterId, onlyGroupCodes, cancellationToken)
            : [];
        // The one combat switch, read at push (ET-472): turning it off withdraws a run's combat on its next push.
        bool combatWithheld = pendingRuns.Count > 0 && !(await shares.LoadAsync(cancellationToken)).IsShared(MetricKind.Dps);
        IReadOnlyDictionary<Guid, RunCombatTimeline> timelines = combatWithheld
            ? new Dictionary<Guid, RunCombatTimeline>()
            : await _LoadCombatTimelinesAsync([.. pendingRuns.Select(run => run.Id)], cancellationToken);
        var pushedRunIds = new HashSet<Guid>();
        foreach (Run run in pendingRuns)
        {
            var payload = new RunWirePayload
            {
                Run = RunWireData.FromEntity(run, timelines.GetValueOrDefault(run.Id), combatWithheld),
                SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var push = await client.PushAsync(serverAddress, payload, characterId, cancellationToken);
            if (!push.Accepted)
                return (false, push.Message);
            await _MarkSyncedAsync(run.Id, serverAddress, push.LastPushedAtUtc, cancellationToken);
            // "queued" turning into "published" on the row is this write, not the pull below.
            await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
            pushedRunIds.Add(run.Id);
        }

        if (groupCodes.Length > 0)
        {
            var pull = await client.PullAsync(serverAddress, groupCodes, waterline, characterId, cancellationToken);
            if (!pull.Accepted)
                return (false, pull.Message);
            await applier.ApplyAsync(serverAddress, pull.Runs, pushedRunIds, cancellationToken);
        }
        return (true, "Runs synchronized.");
    }

    /// <summary>Pending for THIS server, never pending as such: a run queued for another coupled server must not
    /// travel here because a sync happened to run first.</summary>
    private async Task<IReadOnlyList<Run>> _LoadPendingAsync(string serverAddress, long characterId,
        string[]? onlyGroupCodes, CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<Run> pending = db.Set<Run>().AsNoTracking().Where(run =>
            run.CharacterId == characterId && run.SyncState == RunSyncState.Pending && run.SyncServerAddress == serverAddress);
        if (onlyGroupCodes is not null)
            pending = pending.Where(run => run.GroupCode != null && onlyGroupCodes.Contains(run.GroupCode));
        return await _IncludeGraph(pending).ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Run>> _LoadGroupRunsAsync(string[]? onlyGroupCodes, CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<Run> grouped = db.Set<Run>().AsNoTracking().Where(run => run.GroupCode != null);
        if (onlyGroupCodes is not null)
            grouped = grouped.Where(run => run.GroupCode != null && onlyGroupCodes.Contains(run.GroupCode));
        return await grouped.ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyDictionary<Guid, RunCombatTimeline>> _LoadCombatTimelinesAsync(Guid[] runIds,
        CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<RunCombatTimeline>().AsNoTracking()
            .Where(timeline => runIds.Contains(timeline.RunId))
            .Include(timeline => timeline.Series)
            .Include(timeline => timeline.HitTallies)
            .ToDictionaryAsync(timeline => timeline.RunId, cancellationToken);
    }

    private async Task _MarkSyncedAsync(Guid runId, string serverAddress, DateTime? lastPushedAtUtc, CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<Run>().Where(run => run.Id == runId).ExecuteUpdateAsync(properties => properties
            .SetProperty(run => run.SyncState, RunSyncState.Synced)
            .SetProperty(run => run.SyncServerAddress, serverAddress)
            .SetProperty(run => run.LastPushedAtUtc, lastPushedAtUtc), cancellationToken);
    }

    private static IQueryable<Run> _IncludeGraph(IQueryable<Run> runs) => runs
        .Include(run => run.LootCaptures).ThenInclude(capture => capture.Entries)
        .Include(run => run.LootCaptures).ThenInclude(capture => capture.UnrecognisedLines)
        .Include(run => run.BountyEntries)
        .Include(run => run.EnemyObservations)
        .Include(run => run.Parameters)
        .Include(run => run.MiningEntries)
        .Include(run => run.AttendanceEntries);
}
