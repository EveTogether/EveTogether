using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Market.Repositories.Implementations;

internal sealed class MarketPriceRepository(IDbContextFactory<SharedDbContext> contextFactory) : IMarketPriceRepository, ISingletonService
{
    public async Task ReplaceAllAsync(IReadOnlyCollection<LocalMarketPrice> prices, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Full hourly snapshot: clear and re-insert rather than diff ~13k rows.
        await db.Set<LocalMarketPrice>().ExecuteDeleteAsync(cancellationToken);
        db.Set<LocalMarketPrice>().AddRange(prices);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, double>> GetAveragePricesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<LocalMarketPrice>()
            .Where(p => typeIds.Contains(p.TypeId))
            .ToDictionaryAsync(p => p.TypeId, p => p.AveragePrice, cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<LocalMarketPrice>().CountAsync(cancellationToken);
    }
}
