using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>Loot at today's price for the earnings statistic (ET-513), by the rule "Re-value at current prices"
/// follows (RunPriceSnapshots): a blueprint at its build appraisal while that is on, one whose build cannot be priced
/// in full unpriced rather than at a market price for something it is not, everything else at the cached average.</summary>
internal static class TodaysLootPrices
{
    public static async Task<Func<int, decimal?>> ForAsync(IMarketPriceRepository marketPrices,
        IBlueprintAppraisalService blueprints, IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken)
    {
        if (typeIds.Count == 0)
            return _ => null;

        IReadOnlyDictionary<int, decimal?> appraised = await blueprints.GetLootValuesAsync(typeIds, cancellationToken);
        IReadOnlyDictionary<int, double> live = await marketPrices.GetAveragePricesAsync(typeIds, cancellationToken);
        return typeId => appraised.TryGetValue(typeId, out decimal? blueprint)
            ? blueprint
            : live.TryGetValue(typeId, out double price) ? (decimal)price : null;
    }
}
