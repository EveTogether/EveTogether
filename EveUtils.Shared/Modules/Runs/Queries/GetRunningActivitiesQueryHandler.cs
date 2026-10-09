using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Tally;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetRunningActivitiesQueryHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices, ISdeAccessor sde)
    : IQueryHandler<GetRunningActivitiesQuery, Result<IReadOnlyList<RunningActivityDto>>>
{
    public async Task<Result<IReadOnlyList<RunningActivityDto>>> Handle(
        GetRunningActivitiesQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<Run> runs = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Running && !run.DeletedAtUtc.HasValue)
            .Include(run => run.LootCaptures).ThenInclude(capture => capture.Entries)
            .Include(run => run.BountyEntries)
            .Include(run => run.MiningEntries)
            .Include(run => run.Parameters)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        if (runs.Count == 0)
            return Result<IReadOnlyList<RunningActivityDto>>.Success([]);

        MiningOreTypes ores = RunIskFactsReader.OresOf(runs, sde);
        ILookup<Guid, LinkedLoss> lossesByRun = await RunIskFactsReader.LinkedLossesAsync(db,
            [.. runs.Select(run => run.Id)], cancellationToken);
        IReadOnlyList<int> priceTypeIds = RunIskFactsReader.PricedTypeIds(runs, runs.SelectMany(run => run.Parameters), ores,
            lossesByRun.SelectMany(group => group));
        IReadOnlyDictionary<int, double> prices = priceTypeIds.Count == 0
            ? new Dictionary<int, double>()
            : await marketPrices.GetAveragePricesAsync(priceTypeIds, cancellationToken);

        DateTime nowUtc = DateTime.UtcNow;
        List<RunningActivityDto> activities = [.. runs
            .GroupBy(run => run.GroupCode ?? run.Id.ToString())
            .Select(activity =>
            {
                // Ordered by run id the way a saved activity is, so a mission reward every own toon carries counts once.
                RunIskFacts[] facts = [.. activity.OrderBy(run => run.Id)
                    .Select(run => RunIskFactsReader.From(run, run.Parameters, prices, ores, lossesByRun[run.Id], ChargeTypes.Of(sde)))];
                decimal[] loot = [.. facts.Select(run => run.LootIskNet).OfType<decimal>()];
                return new RunningActivityDto(
                    activity.Key,
                    activity.MinBy(run => run.StartedAtUtc)?.SolarSystemId,
                    facts.Sum(run => run.BountyIsk),
                    activity.Sum(run => run.BountyEntries.Count),
                    loot.Length == 0 ? null : loot.Sum(),
                    IskContributors.Breakdown(facts, nowUtc));
            })];
        return Result<IReadOnlyList<RunningActivityDto>>.Success(activities);
    }
}
