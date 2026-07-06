using EveUtils.Shared.Modules.Market.Entities;

namespace EveUtils.Shared.Modules.Market.Repositories;

/// <summary>Cached ESI market prices: the hourly refresh writes them, the fit-value estimate reads them.</summary>
public interface IMarketPriceRepository
{
    /// <summary>Replaces the cache with a fresh snapshot from <c>GET /markets/prices/</c>.</summary>
    Task ReplaceAllAsync(IReadOnlyCollection<LocalMarketPrice> prices, CancellationToken cancellationToken = default);

    /// <summary>The average price per requested type id (missing ids are absent), for estimating a fit's value.</summary>
    Task<IReadOnlyDictionary<int, double>> GetAveragePricesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken = default);

    /// <summary>How many prices are cached (0 = not refreshed yet).</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
