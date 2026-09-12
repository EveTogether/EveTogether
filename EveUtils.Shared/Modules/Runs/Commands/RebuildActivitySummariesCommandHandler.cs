using System.Security.Cryptography;
using System.Text;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Tally;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class RebuildActivitySummariesCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices, IEventBus eventBus)
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
            .ToListAsync(cancellationToken);
        // Read on their own rather than as one more Include: a fourth sibling collection in the same join would repeat
        // every loot line once more per parameter of its run.
        ILookup<Guid, RunParameter> parametersByRun = (await db.Set<RunParameter>()
            .AsNoTracking()
            .Where(parameter => saved.Any(run => run.Id == parameter.RunId))
            .ToListAsync(cancellationToken)).ToLookup(parameter => parameter.RunId);

        // Valuation always goes through ET's own type-id lookup (the LocalMarketPrice cache), never the clipboard's
        // own ISK column — the same rule RunLootViewModel._LoadPricesAsync follows for the running run.
        List<int> lootTypeIds = [.. runs
            .SelectMany(run => run.LootCaptures)
            .Where(capture => !capture.IsExcluded)
            .SelectMany(capture => capture.Entries)
            .Select(entry => entry.ItemTypeId)
            .Distinct()];
        IReadOnlyDictionary<int, double> prices = await marketPrices.GetAveragePricesAsync(lootTypeIds, cancellationToken);

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
            ActivitySummary built = _Build(activity.Key, activity.ToArray(), parametersByRun, prices);
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

    /// <summary>An id derived from the key the activity is grouped on rather than drawn fresh. A row that is still
    /// standing keeps whatever id it already has (ET-215); this is for the row built anew — above all an activity
    /// deleted whole, whose row went with it, and then restored: it comes back under the id a screen may still be
    /// holding for it, instead of under a new one nobody knows (ET-222).</summary>
    private static Guid _IdFor(string activityKey) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"activity-summary:{activityKey}")).AsSpan(0, 16));

    private static ActivitySummary _Build(string activityKey, IReadOnlyList<Run> runs,
        ILookup<Guid, RunParameter> parametersByRun, IReadOnlyDictionary<int, double> prices)
    {
        Run source = runs.OrderBy(run => run.StartedAtUtc).ThenBy(run => run.Id).First();
        DateTime startedAtUtc = runs.Min(run => run.StartedAtUtc);
        DateTime? stoppedAtUtc = runs.All(run => run.StoppedAtUtc is not null)
            ? runs.Max(run => run.StoppedAtUtc)
            : null;
        List<LootTallyLine> loot = [.. runs.SelectMany(run => LootTally.Count(RunIskFactsReader.Tally(run)))];
        decimal? gained = RunIskFactsReader.KnownLootValue(loot, LootKind.Gained, prices);
        decimal? lost = RunIskFactsReader.KnownLootValue(loot, LootKind.Lost, prices);
        // Per run, then added up over the activity by each contributor — the same breakdown the open run window and
        // UNFINISHED make, stored so every screen reads this one and none of them adds figures of its own (ET-256).
        IskBreakdown isk = IskContributors.Breakdown(
            [.. runs.Select(run => RunIskFactsReader.From(run, parametersByRun[run.Id], prices))], DateTime.UtcNow);
        // Runs, for the PayoutEligibleCount column: how many eligible runs the activity holds.
        int payoutEligibleCount = runs.Count(run => run.IsPayoutEligible);
        // Distinct characters, for the expected payout: they differ because ET-130 lets one character hold more than
        // one eligible run in the same activity, and dividing by runs there would shrink everybody's share.
        int payoutEligibleCharacterCount = runs.Where(run => run.IsPayoutEligible)
            .Select(run => run.CharacterId).Distinct().Count();

        return new ActivitySummary
        {
            Id = _IdFor(activityKey),
            GroupCode = source.GroupCode,
            RunId = source.GroupCode is null ? source.Id : null,
            ActivityKind = source.ActivityKind,
            SiteTypeId = source.SiteTypeId,
            SiteName = source.SiteName,
            SignatureGroupSnapshot = source.SignatureGroupSnapshot,
            SolarSystemId = source.SolarSystemId,
            StartedAtUtc = startedAtUtc,
            StoppedAtUtc = stoppedAtUtc,
            DurationSeconds = stoppedAtUtc is null ? 0 : Math.Max(0, (int)(stoppedAtUtc.Value - startedAtUtc).TotalSeconds),
            RunsIncluded = runs.Count,
            ParticipantCount = runs.Select(run => run.CharacterId).Distinct().Count(),
            PayoutEligibleCount = payoutEligibleCount,
            LootIskGained = gained,
            LootIskLost = lost,
            LootIskNet = gained is null && lost is null ? null : gained.GetValueOrDefault() - lost.GetValueOrDefault(),
            LootEntriesWithoutPrice = loot.Count(line => !prices.ContainsKey(line.ItemTypeId)),
            LootItemCount = checked((int)loot.Sum(line => line.Quantity.GetValueOrDefault())),
            // The volume column of an EVE inventory is already the volume of the whole stack (measured: 2 filaments = 0,20 m3).
            LootVolume = loot.Sum(line => line.Volume.GetValueOrDefault()),
            BountyIsk = runs.SelectMany(run => run.BountyEntries).Sum(entry => entry.Isk),
            // Same equal split RunPayoutSplit.Apply makes over the running run's participants — over eligible
            // characters here, since this read model has no per-participant rows to divide onto.
            ExpectedPayoutIsk = payoutEligibleCharacterCount > 0 && gained is { } total ? total / payoutEligibleCharacterCount : 0m,
            TotalIsk = isk.HasFigure ? isk.Total : null,
            IskContributions = StoredIskBreakdown.Write(isk),
            IskSources = IskContributors.Signature,
            EnemyTypeCount = runs.SelectMany(run => run.EnemyObservations).Select(observation => observation.EnemyTypeId).Distinct().Count(),
            CompletenessUnknown = source.GroupCode is not null,
            ComputedAtUtc = DateTime.UtcNow,
            SourceRevisionSum = checked(runs.Sum(run => run.Revision))
        };
    }
}
