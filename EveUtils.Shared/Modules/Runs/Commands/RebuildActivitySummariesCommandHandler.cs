using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

internal sealed class RebuildActivitySummariesCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices)
    : ICommandHandler<RebuildActivitySummariesCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RebuildActivitySummariesCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<Run> runs = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Saved && !run.DeletedAtUtc.HasValue)
            .Include(run => run.LootCaptures)
                .ThenInclude(capture => capture.Entries)
            .Include(run => run.BountyEntries)
            .Include(run => run.EnemyObservations)
            .ToListAsync(cancellationToken);

        // Valuation always goes through ET's own type-id lookup (the LocalMarketPrice cache), never the clipboard's
        // own ISK column — the same rule RunLootViewModel._LoadPricesAsync follows for the running run.
        List<int> lootTypeIds = [.. runs
            .SelectMany(run => run.LootCaptures)
            .Where(capture => !capture.IsExcluded)
            .SelectMany(capture => capture.Entries)
            .Select(entry => entry.ItemTypeId)
            .Distinct()];
        IReadOnlyDictionary<int, double> prices = await marketPrices.GetAveragePricesAsync(lootTypeIds, cancellationToken);

        db.Set<ActivitySummary>().RemoveRange(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
        foreach (IGrouping<string, Run> activity in runs.GroupBy(run => run.GroupCode ?? run.Id.ToString()))
            db.Set<ActivitySummary>().Add(_Build(activity.ToArray(), prices));

        await db.SaveChangesAsync(cancellationToken);
        return Result<int>.Success(runs.Count);
    }

    private static ActivitySummary _Build(IReadOnlyList<Run> runs, IReadOnlyDictionary<int, double> prices)
    {
        Run source = runs.OrderBy(run => run.StartedAtUtc).ThenBy(run => run.Id).First();
        DateTime startedAtUtc = runs.Min(run => run.StartedAtUtc);
        DateTime? stoppedAtUtc = runs.All(run => run.StoppedAtUtc is not null)
            ? runs.Max(run => run.StoppedAtUtc)
            : null;
        // An excluded capture stays visible on its run and counts towards nothing.
        List<RunLootEntry> loot = runs.SelectMany(run => run.LootCaptures)
            .Where(capture => !capture.IsExcluded)
            .SelectMany(capture => capture.Entries)
            .ToList();
        decimal? gained = _KnownLootValue(loot, LootKind.Gained, prices);
        decimal? lost = _KnownLootValue(loot, LootKind.Lost, prices);
        int payoutEligibleCount = runs.Count(run => run.IsPayoutEligible);
        // Runs, not characters: ET-130 allows one character to hold more than one payout-eligible run in the same
        // activity, and dividing by runs there would halve everybody's share without anyone noticing.
        int payoutEligibleCharacterCount = runs.Where(run => run.IsPayoutEligible)
            .Select(run => run.CharacterId).Distinct().Count();

        return new ActivitySummary
        {
            Id = Guid.CreateVersion7(),
            GroupCode = source.GroupCode,
            RunId = source.GroupCode is null ? source.Id : null,
            ActivityKind = source.ActivityKind,
            SiteTypeId = source.SiteTypeId,
            SiteName = source.SiteName,
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
            LootEntriesWithoutPrice = loot.Count(entry => !prices.ContainsKey(entry.ItemTypeId)),
            LootItemCount = checked((int)loot.Sum(entry => entry.Quantity.GetValueOrDefault())),
            // The volume column of an EVE inventory is already the volume of the whole stack (measured: 2 filaments = 0,20 m3).
            LootVolume = loot.Sum(entry => entry.Volume.GetValueOrDefault()),
            BountyIsk = runs.SelectMany(run => run.BountyEntries).Sum(entry => entry.Isk),
            // Same equal split RunPayoutSplit.Apply makes over the running run's participants — over eligible
            // characters here, since this read model has no per-participant rows to divide onto.
            ExpectedPayoutIsk = payoutEligibleCharacterCount > 0 && gained is { } total ? total / payoutEligibleCharacterCount : 0m,
            EnemyTypeCount = runs.SelectMany(run => run.EnemyObservations).Select(observation => observation.EnemyTypeId).Distinct().Count(),
            CompletenessUnknown = source.GroupCode is not null,
            ComputedAtUtc = DateTime.UtcNow,
            SourceRevisionSum = checked(runs.Sum(run => run.Revision))
        };
    }

    private static decimal? _KnownLootValue(IEnumerable<RunLootEntry> loot, LootKind lootKind, IReadOnlyDictionary<int, double> prices)
    {
        // GetValueOrDefault(), not ?? 1: a missing quantity counts as zero pieces in LootItemCount above, so it
        // must value as zero here too — the same line as "what the lookup doesn't know doesn't count".
        decimal[] values = [.. loot
            .Where(entry => entry.LootKind == lootKind && prices.ContainsKey(entry.ItemTypeId))
            .Select(entry => (decimal)prices[entry.ItemTypeId] * entry.Quantity.GetValueOrDefault())];
        return values.Length == 0 ? null : values.Sum();
    }
}
