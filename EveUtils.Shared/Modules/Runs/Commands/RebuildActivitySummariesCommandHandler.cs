using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

internal sealed class RebuildActivitySummariesCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
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

        db.Set<ActivitySummary>().RemoveRange(await db.Set<ActivitySummary>().ToListAsync(cancellationToken));
        foreach (IGrouping<string, Run> activity in runs.GroupBy(run => run.GroupCode ?? run.Id.ToString()))
            db.Set<ActivitySummary>().Add(_Build(activity.ToArray()));

        await db.SaveChangesAsync(cancellationToken);
        return Result<int>.Success(runs.Count);
    }

    private static ActivitySummary _Build(IReadOnlyList<Run> runs)
    {
        Run source = runs.OrderBy(run => run.StartedAtUtc).ThenBy(run => run.Id).First();
        DateTime startedAtUtc = runs.Min(run => run.StartedAtUtc);
        DateTime? stoppedAtUtc = runs.All(run => run.StoppedAtUtc is not null)
            ? runs.Max(run => run.StoppedAtUtc)
            : null;
        List<RunLootEntry> loot = runs.SelectMany(run => run.LootCaptures).SelectMany(capture => capture.Entries).ToList();
        decimal? gained = _KnownLootValue(loot, LootKind.Gained);
        decimal? lost = _KnownLootValue(loot, LootKind.Lost);

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
            PayoutEligibleCount = runs.Count(run => run.IsPayoutEligible),
            LootIskGained = gained,
            LootIskLost = lost,
            LootIskNet = gained is null && lost is null ? null : gained.GetValueOrDefault() - lost.GetValueOrDefault(),
            LootEntriesWithoutPrice = loot.Count(entry => entry.ClipboardPrice is null),
            LootItemCount = checked((int)loot.Sum(entry => entry.Quantity.GetValueOrDefault())),
            LootVolume = loot.Sum(entry => entry.Volume.GetValueOrDefault() * entry.Quantity.GetValueOrDefault()),
            BountyIsk = runs.SelectMany(run => run.BountyEntries).Sum(entry => entry.Isk),
            ExpectedPayoutIsk = 0m,
            EnemyTypeCount = runs.SelectMany(run => run.EnemyObservations).Select(observation => observation.EnemyTypeId).Distinct().Count(),
            CompletenessUnknown = source.GroupCode is not null,
            ComputedAtUtc = DateTime.UtcNow,
            SourceRevisionSum = checked(runs.Sum(run => run.Revision))
        };
    }

    private static decimal? _KnownLootValue(IEnumerable<RunLootEntry> loot, LootKind lootKind)
    {
        decimal?[] prices = loot
            .Where(entry => entry.LootKind == lootKind)
            .Select(entry => entry.ClipboardPrice)
            .Where(price => price is not null)
            .ToArray();
        return prices.Length == 0 ? null : prices.Sum(price => price.GetValueOrDefault());
    }
}
