using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Fittings.Repositories.Implementations;

internal sealed class FittingRepository(IDbContextFactory<SharedDbContext> contextFactory) : IFittingRepository
{
    public async Task UpsertAsync(LocalFitting fitting, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (string.IsNullOrEmpty(fitting.ContentHash))
            fitting.ContentHash = FitContentHash.Compute(fitting.RawJson);

        var existing = await db.Set<LocalFitting>()
            .FirstOrDefaultAsync(f => f.OwnerId == fitting.OwnerId && f.EsiFittingId == fitting.EsiFittingId, cancellationToken);

        if (existing is null)
        {
            db.Set<LocalFitting>().Add(fitting);
        }
        else
        {
            existing.Name = fitting.Name;
            existing.ShipTypeId = fitting.ShipTypeId;
            existing.RawJson = fitting.RawJson;
            existing.ImportedAt = fitting.ImportedAt;
            existing.ContentHash = fitting.ContentHash;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LocalFitting?> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<LocalFitting>()
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.ContentHash == contentHash, cancellationToken);
    }

    public async Task BackfillContentHashesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stale = await db.Set<LocalFitting>().Where(f => f.ContentHash == "").ToListAsync(cancellationToken);
        if (stale.Count == 0)
            return;
        foreach (var fit in stale)
            fit.ContentHash = FitContentHash.Compute(fit.RawJson);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalFitting>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<LocalFitting>()
            .Where(f => f.OwnerId == ownerId)
            .OrderBy(f => f.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalFitting>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<LocalFitting>()
            .OrderBy(f => f.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<LocalFitting?> FindByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<LocalFitting>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
    }

    public async Task<LocalFitting?> FindByEsiIdAsync(string ownerId, int esiFittingId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<LocalFitting>()
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.OwnerId == ownerId && f.EsiFittingId == esiFittingId, cancellationToken);
    }

    public async Task UpdateMetadataAsync(int id, string name, string? description, string? tags, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Set<LocalFitting>().FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (entity is null)
            return;

        // Name/description/tags only — the modules (RawJson) and the content hash are untouched, so the fit keeps its
        // identity: renaming or tagging never makes it a "different" fit.
        entity.Name = name;
        entity.Description = description;
        entity.Tags = tags;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveByEsiIdAsync(string ownerId, int esiFittingId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Set<LocalFitting>()
            .FirstOrDefaultAsync(f => f.OwnerId == ownerId && f.EsiFittingId == esiFittingId, cancellationToken);
        if (entity is not null)
        {
            db.Remove(entity);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RemoveByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Set<LocalFitting>().FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (entity is not null)
        {
            db.Remove(entity);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
