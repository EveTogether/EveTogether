using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Sync.Entities;
using EveUtils.Shared.Modules.Sync.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Sync.Repositories.Implementations;

internal sealed class SyncLogRepository(IDbContextFactory<SharedDbContext> contextFactory) : ISyncLogRepository
{
    public async Task<IReadOnlyList<SyncLog>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<SyncLog>().AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<int> AddAsync(SyncLog syncLog, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<SyncLog>().Add(syncLog);
        await db.SaveChangesAsync(cancellationToken);
        return syncLog.Id;
    }
}
