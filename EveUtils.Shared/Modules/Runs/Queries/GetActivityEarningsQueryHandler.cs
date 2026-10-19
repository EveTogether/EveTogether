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
internal sealed class GetActivityEarningsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory, ISdeAccessor sde,
    IMarketPriceRepository marketPrices, IBlueprintAppraisalService blueprints)
    : IQueryHandler<GetActivityEarningsQuery, Result<IReadOnlyList<ActivityEarningsDto>>>
{
    public async Task<Result<IReadOnlyList<ActivityEarningsDto>>> Handle(GetActivityEarningsQuery query,
        CancellationToken cancellationToken = default)
    {
        DateTime nowUtc = DateTime.UtcNow;
        EarningsPolicy defaults = EarningsPolicy.Default;
        TimeSpan widest = query.Window
                          ?? (defaults.FallbackWindow is { } fallback && fallback > defaults.Window ? fallback : defaults.Window);

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IReadOnlyList<ActivityEarningsSample> samples = await LocalEarningsSamples.ReadAsync(db, sde, nowUtc - widest, nowUtc,
            cancellationToken);
        ActivityEarningsSample[] asked = [.. samples.Where(sample => query.Key is null || sample.Key == query.Key)];
        Func<int, decimal?> unitPrice = await TodaysLootPrices.ForAsync(marketPrices, blueprints, sde,
            [.. asked
                .SelectMany(sample => sample.Sample.LootGained.Concat(sample.Sample.LootLost).Concat(sample.Sample.Mined))
                .Select(line => line.TypeId)
                .Distinct()],
            cancellationToken);

        return Result<IReadOnlyList<ActivityEarningsDto>>.Success(
        [
            .. asked
                .GroupBy(sample => sample.Key)
                .Select(activity => new ActivityEarningsDto
                {
                    Key = activity.Key,
                    SiteName = activity.MaxBy(sample => sample.Sample.CompletedAtUtc)?.SiteName,
                    Figures = EarningsStatistics.Compute([.. activity.Select(sample => sample.Sample)], unitPrice,
                        _PolicyFor(activity.Key, query.Window), nowUtc)
                })
                .OrderByDescending(activity => activity.Figures.EffectiveRunCount)
                .ThenBy(activity => activity.Key.Code, StringComparer.Ordinal)
        ]);
    }

    private static EarningsPolicy _PolicyFor(ActivityStatsKey key, TimeSpan? window) =>
        window is { } asked
            ? EarningsPolicy.For(key.Kind) with { Window = asked, FallbackWindow = null }
            : EarningsPolicy.For(key.Kind);
}
