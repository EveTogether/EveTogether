using EveUtils.Shared.Modules.Market.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Market;

/// <summary>
/// Client-only module: the cached ESI market prices. The entity lives in <c>Shared</c> so the migration
/// plumbing can reach the EF model; only the <c>ClientDbContext</c> applies this config.
/// </summary>
public static class MarketModule
{
    public static void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfiguration(new LocalMarketPriceConfiguration());
}
