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
internal sealed class GetActivityLootTableQueryHandler(IDbContextFactory<ClientDbContext> contextFactory, ISdeAccessor sde,
    IMarketPriceRepository marketPrices, IBlueprintAppraisalService blueprints)
    : IQueryHandler<GetActivityLootTableQuery, Result<LootTableDto>>
{
    public async Task<Result<LootTableDto>> Handle(GetActivityLootTableQuery query, CancellationToken cancellationToken = default)
    {
        LootTablePolicy policy = LootTablePolicy.Default;
        DateTime nowUtc = DateTime.UtcNow;
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IReadOnlyList<ActivityEarningsSample> samples = await LocalEarningsSamples.ReadAsync(db, sde, nowUtc - policy.LongWindow,
            nowUtc, cancellationToken);
        EarningsSample[] ofKey = [.. samples.Where(sample => sample.Key == query.Key).Select(sample => sample.Sample)];
        Func<int, decimal?> unitPrice = await TodaysLootPrices.ForAsync(marketPrices, blueprints, sde,
            [.. ofKey.SelectMany(sample => sample.LootGained).Select(line => line.TypeId).Distinct()], cancellationToken);
        return Result<LootTableDto>.Success(LootTableStatistics.Compute(query.Key, ofKey, LootRarity.Of(sde), unitPrice, policy,
            query.Window, nowUtc, sde.Version?.ReleaseDate.UtcDateTime));
    }
}
