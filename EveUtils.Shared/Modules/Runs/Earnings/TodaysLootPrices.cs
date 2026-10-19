using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>Loot and ore at today's price for the earnings statistic (ET-513), by the rule "Re-value at current prices"
/// follows (RunPriceSnapshots): a blueprint at its build appraisal while that is on, one whose build cannot be priced
/// in full unpriced rather than at a market price for something it is not, Mutanite at its fixed NPC price
/// (MiningValuation), everything else at the cached average.</summary>
internal static class TodaysLootPrices
{
    public static async Task<Func<int, decimal?>> ForAsync(IMarketPriceRepository marketPrices,
        IBlueprintAppraisalService blueprints, ISdeAccessor sde, IReadOnlyCollection<int> typeIds,
        CancellationToken cancellationToken)
    {
        if (typeIds.Count == 0)
            return _ => null;

        IReadOnlyDictionary<int, decimal?> appraised = await blueprints.GetLootValuesAsync(typeIds, cancellationToken);
        IReadOnlyDictionary<int, double> live = await marketPrices.GetAveragePricesAsync(typeIds, cancellationToken);
        HashSet<int> mutanite = [.. typeIds.Where(typeId => MiningValuation.IsMutanite(sde, typeId))];
        return typeId => appraised.TryGetValue(typeId, out decimal? blueprint)
            ? blueprint
            : MiningValuation.UnitPrice(typeId, mutanite.Contains(typeId), live);
    }
}
