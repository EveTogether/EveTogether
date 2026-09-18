using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class RebuildActivitySummariesCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices, ISdeAccessor sde, IEventBus eventBus)
    : ICommandHandler<RebuildActivitySummariesCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RebuildActivitySummariesCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (command.OnlyWhenOutdated && !await db.Set<ActivitySummary>()
                .AnyAsync(summary => summary.IskSources != IskContributors.Signature, cancellationToken))
            return Result<int>.Success(0);

        IQueryable<Run> saved = db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Saved && !run.DeletedAtUtc.HasValue);
        IQueryable<ActivitySummary> replaced = db.Set<ActivitySummary>();
        string? groupCode = null;
        if (command.ActivityOfRunId is { } runId)
        {
            // The same key the full rebuild groups on below: the group code, or the run itself when it has none.
            groupCode = await db.Set<Run>().Where(run => run.Id == runId)
                .Select(run => run.GroupCode).FirstOrDefaultAsync(cancellationToken);
            saved = groupCode is null ? saved.Where(run => run.Id == runId) : saved.Where(run => run.GroupCode == groupCode);
            replaced = groupCode is null
                ? replaced.Where(summary => summary.RunId == runId)
                : replaced.Where(summary => summary.GroupCode == groupCode);
        }

        List<Run> runs = await saved
            .Include(run => run.LootCaptures)
                .ThenInclude(capture => capture.Entries)
            .Include(run => run.BountyEntries)
            .Include(run => run.EnemyObservations)
            .Include(run => run.MiningEntries)
            // One query per collection (ET-287): in one join the collections multiply into each other — every loot
            // line repeated once per bounty line and once per enemy seen — which on a real store was most of SAVE's
            // multi-second freeze (EF's MultipleCollectionIncludeWarning).
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        // Read on their own rather than as one more Include: a fourth sibling collection in the same join would repeat
        // every loot line once more per parameter of its run.
        ILookup<Guid, RunParameter> parametersByRun = (await db.Set<RunParameter>()
            .AsNoTracking()
            .Where(parameter => saved.Any(run => run.Id == parameter.RunId))
            .ToListAsync(cancellationToken)).ToLookup(parameter => parameter.RunId);

        // Valuation always goes through ET's own type-id lookup (the LocalMarketPrice cache), never the clipboard's
        // own ISK column — the same rule RunLootViewModel._LoadPricesAsync follows for the running run.
        MiningOreTypes ores = RunIskFactsReader.OresOf(runs, sde);
        IReadOnlyDictionary<int, double> prices = await marketPrices.GetAveragePricesAsync(
            [.. RunIskFactsReader.PricedTypeIds(runs, parametersByRun.SelectMany(group => group), ores)], cancellationToken);

        // Updated in place rather than deleted and re-added, so an activity keeps its summary id across rebuilds and a
        // screen that opened it by that id — the detail screen, an overview row — still finds it after a save or a
        // correction (ET-215).
        List<ActivitySummary> stale = await replaced.ToListAsync(cancellationToken);
        db.Set<ActivitySummary>().RemoveRange(stale);
        Dictionary<string, ActivitySummary> existing = stale
            .GroupBy(summary => summary.GroupCode ?? $"{summary.RunId}")
            .ToDictionary(group => group.Key, group => group.First());
        foreach (IGrouping<string, Run> activity in runs.GroupBy(run => run.GroupCode ?? run.Id.ToString()))
        {
            ActivitySummary built = ActivitySummaryBuilder.Build(activity.Key, activity.ToArray(), parametersByRun, prices, ores);
            if (existing.Remove(activity.Key, out ActivitySummary? kept))
            {
                built.Id = kept.Id;
                db.Entry(kept).State = EntityState.Modified;
                db.Entry(kept).CurrentValues.SetValues(built);
            }
            else
                db.Set<ActivitySummary>().Add(built);
        }

        await db.SaveChangesAsync(cancellationToken);
        // The summaries are what the runs overview and the dashboard read, so a rebuild changes what they show in its
        // own right — and it is the only change that lands after a group SAVE (ET-210), whose runs each skip it.
        await eventBus.PublishAsync(new RunsChangedEvent(command.ActivityOfRunId, groupCode), EventTarget.Local, cancellationToken);
        return Result<int>.Success(runs.Count);
    }
}
