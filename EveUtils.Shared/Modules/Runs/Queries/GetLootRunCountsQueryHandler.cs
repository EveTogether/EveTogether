using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetLootRunCountsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory, ISdeAccessor sde,
    ActivityEarningsBook book)
    : IQueryHandler<GetLootRunCountsQuery, Result<IReadOnlyDictionary<ActivityStatsKey, int>>>
{
    public async Task<Result<IReadOnlyDictionary<ActivityStatsKey, int>>> Handle(GetLootRunCountsQuery query,
        CancellationToken cancellationToken = default)
    {
        LootTablePolicy policy = LootTablePolicy.Default;
        DateTime nowUtc = DateTime.UtcNow;
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IReadOnlyList<ActivityEarningsSample> samples = await book.ReadAsync(db, sde, nowUtc - policy.LongWindow, nowUtc, cancellationToken);
        return Result<IReadOnlyDictionary<ActivityStatsKey, int>>.Success(samples
            .GroupBy(sample => sample.Key)
            .Select(activity => (activity.Key, Count: LootTableStatistics.CountedRunCount(activity.Select(sample => sample.Sample), policy, nowUtc)))
            .Where(activity => activity.Count > 0)
            .ToDictionary(activity => activity.Key, activity => activity.Count));
    }
}
