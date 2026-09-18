using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetActivityDetailQueryHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices, ISdeAccessor sde)
    : IQueryHandler<GetActivityDetailQuery, Result<ActivityDetailDto>>
{
    public async Task<Result<ActivityDetailDto>> Handle(GetActivityDetailQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        ActivitySummary? summary = await db.Set<ActivitySummary>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.ActivitySummaryId, cancellationToken);
        if (summary is null)
            return Result<ActivityDetailDto>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The activity no longer exists.", "Runs"));

        // The same runs RebuildActivitySummariesCommandHandler grouped into this row — read straight from Run, not
        // through RunningRunLookup or any other guess at which run is meant.
        List<Run> runs = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Saved && !run.DeletedAtUtc.HasValue
                          && (summary.GroupCode != null ? run.GroupCode == summary.GroupCode : run.Id == summary.RunId))
            .ToListAsync(cancellationToken);
        List<Guid> runIds = [.. runs.Select(run => run.Id)];

        // Four collections queried separately by RunId rather than as parallel Includes on Run: EF folds sibling
        // collection includes into one join and multiplies them against each other — a six-run fleet activity with
        // twenty loot lines, five bounties, ten sightings and three parameters per run would pull thousands of rows,
        // each loot line repeated once per bounty/sighting/parameter combination, for what is a few hundred rows.
        ILookup<Guid, RunLootCapture> lootByRun = (await db.Set<RunLootCapture>()
            .AsNoTracking()
            .Where(capture => runIds.Contains(capture.RunId))
            .Include(capture => capture.Entries)
            .ToListAsync(cancellationToken)).ToLookup(capture => capture.RunId);
        List<RunBountyEntry> bountyEntries = await db.Set<RunBountyEntry>()
            .AsNoTracking().Where(entry => runIds.Contains(entry.RunId)).ToListAsync(cancellationToken);
        List<RunEnemyObservation> enemyObservations = await db.Set<RunEnemyObservation>()
            .AsNoTracking().Where(observation => runIds.Contains(observation.RunId)).ToListAsync(cancellationToken);
        List<RunParameter> parameters = await db.Set<RunParameter>()
            .AsNoTracking().Where(parameter => runIds.Contains(parameter.RunId)).ToListAsync(cancellationToken);
        List<RunMiningEntry> miningEntries = await db.Set<RunMiningEntry>()
            .AsNoTracking().Where(entry => runIds.Contains(entry.RunId)).ToListAsync(cancellationToken);

        // Every run of the group carries the same attendance decision (ET-230); the newest one is the one that counts,
        // and only its list is read.
        RunAttendanceDecision? attendance = null;
        if (runs.Where(run => run.AttendanceSetAtUtc.HasValue).MaxBy(run => run.AttendanceSetAtUtc) is { } decided)
        {
            foreach (RunAttendanceEntry entry in await db.Set<RunAttendanceEntry>()
                         .AsNoTracking().Where(entry => entry.RunId == decided.Id).ToListAsync(cancellationToken))
                decided.AttendanceEntries.Add(entry);
            attendance = RunAttendanceDecision.Of(decided);
        }

        long? fleetId = summary.GroupCode is null
            ? null
            : await db.Set<RunGroupOrigin>().AsNoTracking()
                .Where(origin => origin.GroupCode == summary.GroupCode)
                .Select(origin => (long?)origin.FleetId)
                .FirstOrDefaultAsync(cancellationToken);

        // Each run's own share of TOTAL ISK (ET-272), for FLEET's row per character — by the same registry and the
        // same pricing the stored summary was added up with, never a formula of this screen's own.
        ILookup<Guid, RunBountyEntry> bountyByRun = bountyEntries.ToLookup(entry => entry.RunId);
        ILookup<Guid, RunMiningEntry> miningByRun = miningEntries.ToLookup(entry => entry.RunId);
        ILookup<Guid, RunParameter> parametersByRun = parameters.ToLookup(parameter => parameter.RunId);
        foreach (Run run in runs)
        {
            foreach (RunLootCapture capture in lootByRun[run.Id])
                run.LootCaptures.Add(capture);
            foreach (RunBountyEntry entry in bountyByRun[run.Id])
                run.BountyEntries.Add(entry);
            foreach (RunMiningEntry entry in miningByRun[run.Id])
                run.MiningEntries.Add(entry);
        }
        MiningOreTypes ores = RunIskFactsReader.OresOf(runs, sde);
        IReadOnlyDictionary<int, double> prices = await marketPrices.GetAveragePricesAsync(
            [.. RunIskFactsReader.PricedTypeIds(runs, parameters, ores)], cancellationToken);
        DateTime nowUtc = DateTime.UtcNow;
        // The summary's own split, not a second one priced here and now (ET-296): FLEET's rows are what TOTAL ISK
        // above them is the sum of, and two valuations taken minutes apart would not add up to it. A summary built
        // before the split was stored still has none, and is added up here until the startup rebuild reaches it.
        IReadOnlyDictionary<long, IskBreakdown> iskByCharacter =
            StoredIskBreakdown.ReadByCharacter(summary.IskContributionsByCharacter)
            ?? IskContributors.BreakdownByCharacter([.. runs
                .OrderBy(run => run.StartedAtUtc).ThenBy(run => run.Id)
                .Select(run => RunIskFactsReader.From(run, parametersByRun[run.Id], prices, ores))], nowUtc);

        return Result<ActivityDetailDto>.Success(ActivityDetails.ToDto(summary, runs, bountyEntries, enemyObservations,
            parameters, miningEntries, attendance, fleetId, iskByCharacter));
    }
}
