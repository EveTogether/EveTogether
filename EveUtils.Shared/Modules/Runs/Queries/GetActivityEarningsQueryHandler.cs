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
        (DateTime From, DateTime To)? range = query is { FromUtc: { } from, ToUtc: { } to } ? (from, to) : null;
        TimeSpan widest = query.Window
                          ?? (defaults.FallbackWindow is { } fallback && fallback > defaults.Window ? fallback : defaults.Window);

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IReadOnlyList<ActivityEarningsSample> samples = await LocalEarningsSamples.ReadAsync(db, sde,
            range?.From ?? nowUtc - widest, nowUtc, cancellationToken);
        ActivityEarningsSample[] asked = [.. samples
            .Where(sample => query.Key is null || sample.Key == query.Key)
            .Where(sample => range is not { } inRange
                             || (sample.Sample.CompletedAtUtc >= inRange.From && sample.Sample.CompletedAtUtc < inRange.To))];
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
                    LastFlownUtc = activity.Max(sample => sample.Sample.CompletedAtUtc),
                    Figures = EarningsStatistics.Compute([.. activity.Select(sample => sample.Sample)], unitPrice,
                        _PolicyFor(activity.Key, query.Window, range), range?.To ?? nowUtc)
                })
                .OrderByDescending(activity => activity.Figures.EffectiveRunCount)
                .ThenBy(activity => activity.Key.Code, StringComparer.Ordinal)
        ]);
    }

    private static EarningsPolicy _PolicyFor(ActivityStatsKey key, TimeSpan? window, (DateTime From, DateTime To)? range)
    {
        if (range is { } asked)
            return EarningsPolicy.For(key.Kind) with { Window = asked.To - asked.From, FallbackWindow = null };

        return window is { } custom
            ? EarningsPolicy.For(key.Kind) with { Window = custom, FallbackWindow = null }
            : EarningsPolicy.For(key.Kind);
    }
}
