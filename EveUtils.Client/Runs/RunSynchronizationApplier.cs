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

public sealed class RunSynchronizationApplier(IDbContextFactory<ClientDbContext> contextFactory, IDispatcher dispatcher) : IScopedService
{
    /// <summary>Applies what <paramref name="serverAddress"/> handed back. The runs land under a group-mate's own
    /// character id, so the day list shows the whole activity rather than only this machine's half of it.</summary>
    public async Task ApplyAsync(string serverAddress, IReadOnlyList<RunWirePayload> payloads, IReadOnlySet<Guid> pushedRunIds,
        CancellationToken cancellationToken = default)
    {
        if (payloads.Count == 0)
            return;

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        Guid[] runIds = payloads.Select(payload => payload.Run.Id).ToArray();
        HashSet<Guid> protectedRunIds = await db.Set<Run>().AsNoTracking()
            .Where(run => runIds.Contains(run.Id) && run.SyncState != RunSyncState.Synced)
            .Select(run => run.Id).ToHashSetAsync(cancellationToken);
        foreach (RunWirePayload payload in payloads)
        {
            Run run = payload.Run.ToEntity();
            if (protectedRunIds.Contains(run.Id) || pushedRunIds.Contains(run.Id))
                continue;

            if (run.DeletedAtUtc is not null)
            {
                await db.Set<Run>().Where(candidate => candidate.Id == run.Id).ExecuteDeleteAsync(cancellationToken);
                continue;
            }

            run.StartedAtUtc = _Anchor(run.StartedAtUtc, payload.SentAtUnixMilliseconds);
            run.StoppedAtUtc = run.StoppedAtUtc is { } stoppedAtUtc ? _Anchor(stoppedAtUtc, payload.SentAtUnixMilliseconds) : null;
            run.SyncState = RunSyncState.Synced;
            run.SyncServerAddress = serverAddress;
            await db.Set<Run>().Where(candidate => candidate.Id == run.Id).ExecuteDeleteAsync(cancellationToken);
            db.Set<Run>().Add(run);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
    }

    private static DateTime _Anchor(DateTime sourceUtc, long sentAtUnixMilliseconds) =>
        AbyssalSpace.AnchorFromWire(new DateTimeOffset(sourceUtc.ToUniversalTime()).ToUnixTimeMilliseconds(), sentAtUnixMilliseconds, DateTime.UtcNow)
        ?? sourceUtc;
}
