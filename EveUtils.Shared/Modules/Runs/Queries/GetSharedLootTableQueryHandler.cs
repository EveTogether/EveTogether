using EveUtils.Shared.Cqrs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetSharedLootTableQueryHandler(ISdeAccessor sde, IMarketPriceRepository marketPrices,
    IBlueprintAppraisalService blueprints) : IQueryHandler<GetSharedLootTableQuery, Result<LootTableDto>>
{
    public async Task<Result<LootTableDto>> Handle(GetSharedLootTableQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Outcomes.Availability is not SharedOutcomesAvailability.Shared
            || query.Loot.Availability is not SharedOutcomesAvailability.Shared)
            return Result<LootTableDto>.Failure(new ResultMessage(MessageSeverity.Error, "TOO_FEW_PILOTS",
                "Too few pilots share this activity to read a loot table from them."));

        if (!SharedOutcomeSamples.Match(query.Outcomes.Outcomes, query.Loot.Outcomes))
            return Result<LootTableDto>.Failure(new ResultMessage(MessageSeverity.Warning,
                GetSharedEarningsQueryHandler.OutcomesChanged, "The shared outcomes changed while they were read."));

        EarningsSample[] samples = [.. SharedOutcomeSamples.Of(query.Outcomes.Outcomes, query.Loot.Outcomes)];
        Func<int, decimal?> unitPrice = await TodaysLootPrices.ForAsync(marketPrices, blueprints, sde,
            [.. samples.SelectMany(sample => sample.LootGained).Select(line => line.TypeId).Distinct()], cancellationToken);
        return Result<LootTableDto>.Success(LootTableStatistics.Compute(query.Outcomes.Key, samples, LootRarity.Of(sde), unitPrice,
            LootTablePolicy.Default, query.Window, DateTime.UtcNow, sde.Version?.ReleaseDate.UtcDateTime));
    }
}
