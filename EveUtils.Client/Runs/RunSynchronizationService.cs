using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Client.Runs;

public sealed class RunSynchronizationService(
    IDbContextFactory<ClientDbContext> contextFactory,
    IServerRunSyncClient client,
    RunSynchronizationApplier applier) : IScopedService
{
    public async Task<(bool Accepted, string Message)> SynchronizeAsync(string serverAddress, long characterId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Run> localRuns = await _LoadGroupRunsAsync(cancellationToken);
        string[] groupCodes = localRuns.Select(run => run.GroupCode).Where(groupCode => groupCode is not null).Cast<string>().Distinct().ToArray();
        DateTime waterline = localRuns.Where(run => run.LastPushedAtUtc.HasValue)
            .Select(run => run.LastPushedAtUtc.GetValueOrDefault()).DefaultIfEmpty(DateTime.UnixEpoch).Min();

        IReadOnlyList<Run> pendingRuns = await _LoadPendingAsync(serverAddress, characterId, cancellationToken);
        var pushedRunIds = new HashSet<Guid>();
        foreach (Run run in pendingRuns)
        {
            var payload = new RunWirePayload
            {
                Run = RunWireData.FromEntity(run),
                SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var push = await client.PushAsync(serverAddress, payload, characterId, cancellationToken);
            if (!push.Accepted)
                return (false, push.Message);
            await _MarkSyncedAsync(run.Id, serverAddress, push.LastPushedAtUtc, cancellationToken);
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
    private async Task<IReadOnlyList<Run>> _LoadPendingAsync(string serverAddress, long characterId, CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await _IncludeGraph(db.Set<Run>().AsNoTracking().Where(run =>
                run.CharacterId == characterId && run.SyncState == RunSyncState.Pending && run.SyncServerAddress == serverAddress))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Run>> _LoadGroupRunsAsync(CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<Run>().AsNoTracking().Where(run => run.GroupCode != null)
            .ToListAsync(cancellationToken);
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
        .Include(run => run.BountyEntries)
        .Include(run => run.EnemyObservations)
        .Include(run => run.Parameters);
}
