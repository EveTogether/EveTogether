using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetActivityYieldQueryHandler(IDbContextFactory<ClientDbContext> contextFactory, ISdeAccessor sde,
    IMarketPriceRepository marketPrices, IBlueprintAppraisalService blueprints)
    : IQueryHandler<GetActivityYieldQuery, Result<ActivityYieldDto?>>
{
    public async Task<Result<ActivityYieldDto?>> Handle(GetActivityYieldQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IReadOnlyList<ActivityEarningsSample> samples = await LocalEarningsSamples.ReadAsync(db, sde, DateTime.MinValue,
            DateTime.UtcNow, cancellationToken, [query.ActivityRef]);
        if (samples.FirstOrDefault(sample => sample.ActivityRef == query.ActivityRef) is not { } activity)
            return Result<ActivityYieldDto?>.Success(null);

        EarningsSample sample = activity.Sample;
        Func<int, decimal?> unitPrice = await TodaysLootPrices.ForAsync(marketPrices, blueprints, sde,
            [.. sample.LootGained.Concat(sample.LootLost).Concat(sample.Mined).Select(line => line.TypeId).Distinct()],
            cancellationToken);
        return Result<ActivityYieldDto?>.Success(new ActivityYieldDto
        {
            Key = activity.Key,
            YieldIsk = EarningsStatistics.YieldOf(sample, unitPrice)
        });
    }
}
