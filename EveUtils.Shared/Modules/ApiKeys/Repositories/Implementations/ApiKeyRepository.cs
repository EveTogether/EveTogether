using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.ApiKeys.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.ApiKeys.Repositories.Implementations;

internal sealed class ApiKeyRepository(IDbContextFactory<SharedDbContext> contextFactory)
    : IApiKeyRepository, IScopedService
{
    public async Task<int> AddAsync(ApiKey key, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        return key.Id;
    }

    public async Task<ApiKey?> FindByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<ApiKey>()
            .AsNoTracking()
            .SingleOrDefaultAsync(k => k.Prefix == prefix, cancellationToken);
    }

    public async Task<ApiKey?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<ApiKey>().AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, cancellationToken);
    }

    /// <summary>Newest first on the key rather than on <c>CreatedAt</c>: SQLite, the default provider,
    /// refuses to sort a <c>DateTimeOffset</c> column.</summary>
    public async Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<ApiKey>().AsNoTracking().OrderByDescending(k => k.Id).ToListAsync(cancellationToken);
    }

    public async Task<bool> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var key = await db.Set<ApiKey>().SingleOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (key is null)
            return false;

        key.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetScopesAsync(int id, string scopes, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var key = await db.Set<ApiKey>().SingleOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (key is null)
            return false;

        key.Scopes = scopes;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task TouchLastUsedAsync(int id, DateTimeOffset usedAt, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<ApiKey>()
            .Where(k => k.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(k => k.LastUsedAt, usedAt), cancellationToken);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<ApiKey>().Where(k => k.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
    }
}
