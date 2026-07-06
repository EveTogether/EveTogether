using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Fittings.Repositories.Implementations;

internal sealed class SharedFitRepository(IDbContextFactory<SharedDbContext> contextFactory) : ISharedFitRepository
{
    public async Task AddAsync(SharedFit fit, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (string.IsNullOrEmpty(fit.ContentHash))
            fit.ContentHash = FitContentHash.Compute(fit.RawJson);
        db.Set<SharedFit>().Add(fit);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SharedFit?> AddOrMatchAsync(SharedFit fit, CancellationToken cancellationToken = default)
    {
        fit.ContentHash = FitContentHash.Compute(fit.RawJson);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Set<SharedFit>()
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.ContentHash == fit.ContentHash, cancellationToken);
        if (existing is not null)
            return existing; // duplicate — do not add a second identical row (caller reports the match)

        db.Set<SharedFit>().Add(fit);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public async Task BackfillContentHashesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stale = await db.Set<SharedFit>().Where(f => f.ContentHash == "").ToListAsync(cancellationToken);
        if (stale.Count == 0)
            return;
        foreach (var fit in stale)
            fit.ContentHash = FitContentHash.Compute(fit.RawJson);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SharedFit>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // SQLite does not support ORDER BY DateTimeOffset — sort by Id DESC instead.
        return await db.Set<SharedFit>()
            .OrderByDescending(f => f.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RemoveAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Set<SharedFit>().FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (entity is null) return false;
        db.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
