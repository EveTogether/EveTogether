using EveUtils.Client.Transport;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Client.Runs;

public sealed class RunSynchronizationService(
    IDbContextFactory<ClientDbContext> contextFactory,
    ServerRunSyncClient client,
    IDispatcher dispatcher) : IScopedService
{
    public async Task<(bool Accepted, string Message)> SynchronizeAsync(string serverAddress, long characterId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Run> localRuns = await _LoadGroupRunsAsync(cancellationToken);
        string[] groupCodes = localRuns.Select(run => run.GroupCode).Where(groupCode => groupCode is not null).Cast<string>().Distinct().ToArray();
        if (groupCodes.Length > 0)
        {
            DateTime waterline = localRuns
                .Where(run => run.GroupCode is not null)
                .Select(run => run.LastPushedAtUtc ?? DateTime.UnixEpoch)
                .Min();
            var pull = await client.PullAsync(serverAddress, groupCodes, waterline, checked((int)characterId), cancellationToken);
            if (!pull.Accepted)
                return (false, pull.Message);
            await _ApplyPulledRunsAsync(pull.Runs, cancellationToken);
        }

        IReadOnlyList<Run> pendingRuns = await _LoadPendingAsync(characterId, cancellationToken);
        foreach (Run run in pendingRuns)
        {
            var payload = new RunWirePayload
            {
                Run = run,
                SentAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var push = await client.PushAsync(serverAddress, payload, checked((int)characterId), cancellationToken);
            if (!push.Accepted)
                return (false, push.Message);
            await _MarkSyncedAsync(run.Id, push.LastPushedAtUtc, cancellationToken);
        }
        return (true, "Runs synchronized.");
    }

    private async Task<IReadOnlyList<Run>> _LoadPendingAsync(long characterId, CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await _IncludeGraph(db.Set<Run>().AsNoTracking().Where(run => run.CharacterId == characterId && run.SyncState == RunSyncState.Pending))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Run>> _LoadGroupRunsAsync(CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<Run>().AsNoTracking().Where(run => run.GroupCode != null)
            .ToListAsync(cancellationToken);
    }

    private async Task _MarkSyncedAsync(Guid runId, DateTime? lastPushedAtUtc, CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<Run>().Where(run => run.Id == runId).ExecuteUpdateAsync(properties => properties
            .SetProperty(run => run.SyncState, RunSyncState.Synced)
            .SetProperty(run => run.LastPushedAtUtc, lastPushedAtUtc), cancellationToken);
    }

    private async Task _ApplyPulledRunsAsync(IReadOnlyList<RunWirePayload> payloads, CancellationToken cancellationToken)
    {
        if (payloads.Count == 0)
            return;

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        foreach (RunWirePayload payload in payloads)
        {
            Run run = payload.Run;
            run.StartedAtUtc = _Anchor(run.StartedAtUtc, payload.SentAtUnixMilliseconds);
            run.StoppedAtUtc = run.StoppedAtUtc is { } stoppedAtUtc ? _Anchor(stoppedAtUtc, payload.SentAtUnixMilliseconds) : null;
            run.SyncState = RunSyncState.Synced;
            await db.Set<Run>().Where(candidate => candidate.Id == run.Id).ExecuteDeleteAsync(cancellationToken);
            db.Set<Run>().Add(run);
        }
        await db.SaveChangesAsync(cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
    }

    private static IQueryable<Run> _IncludeGraph(IQueryable<Run> runs) => runs
        .Include(run => run.LootCaptures).ThenInclude(capture => capture.Entries)
        .Include(run => run.BountyEntries)
        .Include(run => run.EnemyObservations)
        .Include(run => run.Parameters);

    private static DateTime _Anchor(DateTime sourceUtc, long sentAtUnixMilliseconds) =>
        AbyssalSpace.AnchorFromWire(new DateTimeOffset(sourceUtc.ToUniversalTime()).ToUnixTimeMilliseconds(), sentAtUnixMilliseconds, DateTime.UtcNow)
        ?? sourceUtc;
}
