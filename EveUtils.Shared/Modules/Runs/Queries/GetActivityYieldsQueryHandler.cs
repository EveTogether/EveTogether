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
internal sealed class GetActivityYieldsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory, ISdeAccessor sde,
    IMarketPriceRepository marketPrices, IBlueprintAppraisalService blueprints, ActivityEarningsBook book)
    : IQueryHandler<GetActivityYieldsQuery, Result<IReadOnlyDictionary<string, ActivityYieldDto>>>
{
    public async Task<Result<IReadOnlyDictionary<string, ActivityYieldDto>>> Handle(GetActivityYieldsQuery query,
        CancellationToken cancellationToken = default)
    {
        EarningsPolicy defaults = EarningsPolicy.Default;
        DateTime nowUtc = DateTime.UtcNow;
        TimeSpan widest = defaults.FallbackWindow is { } fallback && fallback > defaults.Window ? fallback : defaults.Window;

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        HashSet<string> wanted = [.. query.ActivityRefs];
        ActivityEarningsSample[] asked = [.. (await book.ReadAsync(db, sde, nowUtc - widest, nowUtc, cancellationToken))
            .Where(sample => wanted.Contains(sample.ActivityRef))];
        Func<int, decimal?> unitPrice = await TodaysLootPrices.ForAsync(marketPrices, blueprints, sde,
            [.. asked
                .SelectMany(sample => sample.Sample.LootGained.Concat(sample.Sample.LootLost).Concat(sample.Sample.Mined))
                .Select(line => line.TypeId)
                .Distinct()],
            cancellationToken);

        return Result<IReadOnlyDictionary<string, ActivityYieldDto>>.Success(asked.ToDictionary(sample => sample.ActivityRef,
            sample => new ActivityYieldDto
            {
                Key = sample.Key,
                YieldIsk = EarningsStatistics.YieldOf(sample.Sample, unitPrice),
                CompletedAtUtc = sample.Sample.CompletedAtUtc,
                IsCounted = EarningsStatistics.IsCounted(sample.Sample, EarningsPolicy.For(sample.Key.Kind))
            }));
    }
}
