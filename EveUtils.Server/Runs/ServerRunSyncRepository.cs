using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Server.Runs;

internal sealed class ServerRunSyncRepository(IDbContextFactory<ServerDbContext> contextFactory) : IRunSyncRepository, IScopedService
{
    public async Task<DateTime?> UpsertAsync(Run run, CancellationToken cancellationToken = default)
    {
        await using ServerDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        int? revision = await db.Set<Run>().Where(candidate => candidate.Id == run.Id)
            .Select(candidate => (int?)candidate.Revision).SingleOrDefaultAsync(cancellationToken);
        if (revision is { } currentRevision && currentRevision > run.Revision)
            return null;

        DateTime pushedAtUtc = DateTime.UtcNow;
        run.LastPushedAtUtc = pushedAtUtc;
        await db.Set<Run>().Where(candidate => candidate.Id == run.Id).ExecuteDeleteAsync(cancellationToken);
        db.Set<Run>().Add(run);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return pushedAtUtc;
    }

    public async Task<IReadOnlyList<Run>> ListChangedAsync(
        long characterId, IReadOnlyCollection<string> groupCodes, DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        await using ServerDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await WithChildren(db.Set<Run>()
                .AsNoTracking()
                .Where(run => run.GroupCode != null && groupCodes.Contains(run.GroupCode) &&
                              run.LastPushedAtUtc.HasValue && run.LastPushedAtUtc.Value > sinceUtc &&
                              db.Set<Run>().Any(member => member.CharacterId == characterId &&
                                  member.GroupCode == run.GroupCode && !member.DeletedAtUtc.HasValue)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<long>> ListGroupHoldersAsync(string groupCode, CancellationToken cancellationToken = default)
    {
        await using ServerDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.GroupCode == groupCode && !run.DeletedAtUtc.HasValue)
            .Select(run => run.CharacterId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Run>> ListPublishedAsync(
        long characterId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        await using ServerDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await WithChildren(db.Set<Run>()
                .AsNoTracking()
                .Where(run => run.State == RunState.Saved && !run.DeletedAtUtc.HasValue &&
                              run.StartedAtUtc >= fromUtc && run.StartedAtUtc < toUtc &&
                              (run.CharacterId == characterId ||
                               (run.GroupCode != null && db.Set<Run>().Any(member => member.CharacterId == characterId &&
                                   member.GroupCode == run.GroupCode && !member.DeletedAtUtc.HasValue)))))
            .ToListAsync(cancellationToken);
    }

    /// <summary>One query per collection: in one join the six collections multiply into each other (ET-287), and the
    /// tab read hands back a whole window rather than a delta. The panel's run pane reads through it too.</summary>
    internal static IQueryable<Run> WithChildren(IQueryable<Run> runs) => runs
        .AsSplitQuery()
        .Include(run => run.LootCaptures)
            .ThenInclude(capture => capture.Entries)
        .Include(run => run.BountyEntries)
        .Include(run => run.EnemyObservations)
        .Include(run => run.Parameters)
        .Include(run => run.MiningEntries)
        .Include(run => run.AttendanceEntries);
}
