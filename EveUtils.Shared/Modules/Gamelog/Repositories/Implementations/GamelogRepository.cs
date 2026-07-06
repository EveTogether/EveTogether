using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Gamelog.Entities;
using EveUtils.Shared.Modules.Gamelog.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Gamelog.Repositories.Implementations;

/// <summary>Short-lived context per operation. Reads already scope by <c>OwnerId</c> (pillar 4).</summary>
internal sealed class GamelogRepository(IDbContextFactory<SharedDbContext> contextFactory) : IGamelogRepository
{
    public async Task<int> AddSampleAsync(CombatSample sample, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<CombatSample>().Add(sample);
        await db.SaveChangesAsync(cancellationToken);
        return sample.Id;
    }

    public async Task<IReadOnlyList<CombatSample>> RecentAsync(string ownerId, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Order by Id (monotonic insert order) rather than Timestamp: SQLite can't ORDER BY a
        // DateTimeOffset, and insert order is a faithful "most recent first" across all providers.
        return await db.Set<CombatSample>()
            .AsNoTracking()
            .Where(s => s.OwnerId == ownerId)
            .OrderByDescending(s => s.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }
}
