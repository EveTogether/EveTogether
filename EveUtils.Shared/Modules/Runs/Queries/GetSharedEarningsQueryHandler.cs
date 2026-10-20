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
internal sealed class GetSharedEarningsQueryHandler(ISdeAccessor sde, IMarketPriceRepository marketPrices,
    IBlueprintAppraisalService blueprints) : IQueryHandler<GetSharedEarningsQuery, Result<EarningsFiguresDto>>
{
    public const string OutcomesChanged = "SHARED_OUTCOMES_CHANGED";

    public async Task<Result<EarningsFiguresDto>> Handle(GetSharedEarningsQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Outcomes.Availability is not SharedOutcomesAvailability.Shared
            || query.Loot.Availability is not SharedOutcomesAvailability.Shared)
            return Result<EarningsFiguresDto>.Failure(new ResultMessage(MessageSeverity.Error, "TOO_FEW_PILOTS",
                "Too few pilots share this activity to read figures from them."));

        // Two reads of the same list a moment apart: a share landing between them shifts the rows, and figures built from
        // a loot line that belongs to another outcome are worse than none.
        if (!SharedOutcomeSamples.Match(query.Outcomes.Outcomes, query.Loot.Outcomes))
            return Result<EarningsFiguresDto>.Failure(new ResultMessage(MessageSeverity.Warning, OutcomesChanged,
                "The shared outcomes changed while they were read."));

        EarningsSample[] samples = [.. SharedOutcomeSamples.Of(query.Outcomes.Outcomes, query.Loot.Outcomes)
            .Where(sample => sample.CapWeight > 0)];
        Func<int, decimal?> unitPrice = await TodaysLootPrices.ForAsync(marketPrices, blueprints, sde,
            [.. samples.SelectMany(sample => sample.LootGained.Concat(sample.LootLost).Concat(sample.Mined)).Select(line => line.TypeId).Distinct()],
            cancellationToken);
        EarningsPolicy policy = EarningsPolicy.For(query.Outcomes.Key.Kind) with { Window = query.Window, FallbackWindow = null };
        return Result<EarningsFiguresDto>.Success(
            EarningsStatistics.Compute(samples, unitPrice, policy, DateTime.UtcNow, query.Label));
    }
}
