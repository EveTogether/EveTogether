using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Server.Runs;

internal sealed class ServerRunSyncRepository(IDbContextFactory<ServerDbContext> contextFactory) : IRunSyncRepository, IScopedService
{
    public async Task<DateTime> UpsertAsync(Run run, CancellationToken cancellationToken = default)
    {
        DateTime pushedAtUtc = DateTime.UtcNow;
        run.LastPushedAtUtc = pushedAtUtc;

        await using ServerDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<Run>().Where(candidate => candidate.Id == run.Id).ExecuteDeleteAsync(cancellationToken);
        db.Set<Run>().Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return pushedAtUtc;
    }

    public async Task<IReadOnlyList<Run>> ListChangedAsync(
        IReadOnlyCollection<string> groupCodes, DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        await using ServerDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.GroupCode != null && groupCodes.Contains(run.GroupCode) &&
                          run.LastPushedAtUtc.HasValue && run.LastPushedAtUtc.Value > sinceUtc)
            .Include(run => run.LootCaptures)
                .ThenInclude(capture => capture.Entries)
            .Include(run => run.BountyEntries)
            .Include(run => run.EnemyObservations)
            .Include(run => run.Parameters)
            .ToListAsync(cancellationToken);
    }
}
