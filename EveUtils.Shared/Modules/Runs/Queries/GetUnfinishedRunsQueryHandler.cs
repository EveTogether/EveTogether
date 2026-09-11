using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Tally;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetUnfinishedRunsQueryHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices)
    : IQueryHandler<GetUnfinishedRunsQuery, Result<IReadOnlyList<UnfinishedRunDto>>>
{
    public async Task<Result<IReadOnlyList<UnfinishedRunDto>>> Handle(
        GetUnfinishedRunsQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<Run> runs = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Stopped && !run.DeletedAtUtc.HasValue)
            // What ET-217's TOTAL ISK is built from: captured loot (persisted live, unlike bounty — see
            // UnfinishedRunDto.TotalIsk), the reward parameters a mission carried from the moment it started, and
            // bounty entries for whenever a future run persists them before SAVE too.
            .Include(run => run.LootCaptures).ThenInclude(capture => capture.Entries)
            .Include(run => run.BountyEntries)
            .Include(run => run.Parameters)
            // On the stop where there is one, on the start otherwise: a row without a stop stamp would sort as the
            // oldest thing on screen no matter when it was flown.
            .OrderByDescending(run => run.StoppedAtUtc ?? run.StartedAtUtc)
            .ToListAsync(cancellationToken);

        // Valuation goes through ET's own type-id lookup (the market price cache), never the clipboard's own ISK
        // column — the same rule RebuildActivitySummariesCommandHandler follows for a saved activity's own total.
        List<int> lootTypeIds = [.. runs
            .SelectMany(run => run.LootCaptures)
            .Where(capture => !capture.IsExcluded)
            .SelectMany(capture => capture.Entries)
            .Select(entry => entry.ItemTypeId)
            .Distinct()];
        IReadOnlyDictionary<int, double> prices = lootTypeIds.Count == 0
            ? new Dictionary<int, double>()
            : await marketPrices.GetAveragePricesAsync(lootTypeIds, cancellationToken);

        List<UnfinishedRunDto> dtos = [.. runs.Select(run =>
        {
            (decimal total, bool unknown) = _TotalIsk(run, prices);
            return new UnfinishedRunDto(
                run.Id, run.CharacterId, run.ActivityKind, run.SiteName, run.SignatureGroupSnapshot,
                run.StartedAtUtc, run.StoppedAtUtc, total, unknown);
        })];
        return Result<IReadOnlyList<UnfinishedRunDto>>.Success(dtos);
    }

    // Each unfinished row is already its own run and its own character (RunsOverviewViewModel/UnfinishedRunViewModel
    // show one row per Run, singular site and character text) — so a run that belongs to an ET-210 multi-toon group
    // shows what THAT run itself captured, not the group's combined total: SAVE and DELETE act on this one row alone,
    // and a merged figure would not match what either button actually commits or discards.
    private static (decimal Total, bool Unknown) _TotalIsk(Run run, IReadOnlyDictionary<int, double> prices)
    {
        IReadOnlyList<LootTallyLine> loot = LootTally.Count(
            [.. run.LootCaptures
                .OrderBy(capture => capture.CapturedAtUtc)
                .Select(capture => new LootTallyCapture(capture.Role, capture.IsExcluded,
                    [.. capture.Entries.Select(entry =>
                        new LootTallyLine(entry.ItemTypeId, entry.Quantity, entry.Volume, entry.LootKind))]))]);
        decimal? lootIskNet = _NetIsk(loot, prices);
        decimal bountyIsk = run.BountyEntries.Sum(entry => entry.Isk);
        IReadOnlyList<(RunParameterKey Key, decimal? Amount)> parameters =
            [.. run.Parameters.Select(parameter => (parameter.ParameterKey, parameter.Amount))];
        decimal total = TotalIskCalculator.Total(bountyIsk, lootIskNet, parameters);

        // Unknown only when loot is the sole reason nothing can be said: bounty and rewards are both read straight
        // off storage, never priced, so either one being nonzero already makes the total a real (if possibly
        // loot-incomplete) figure rather than a guess.
        bool unknown = bountyIsk == 0m && TotalIskCalculator.RewardIsk(parameters) == 0m
            && loot.Count > 0 && lootIskNet is null;
        return (total, unknown);
    }

    // Same rule as RebuildActivitySummariesCommandHandler._KnownLootValue: a missing price counts as zero pieces,
    // never as a wrong one.
    private static decimal? _NetIsk(IReadOnlyList<LootTallyLine> loot, IReadOnlyDictionary<int, double> prices)
    {
        decimal? gained = _KnownLootValue(loot, LootKind.Gained, prices);
        decimal? lost = _KnownLootValue(loot, LootKind.Lost, prices);
        return gained is null && lost is null ? null : gained.GetValueOrDefault() - lost.GetValueOrDefault();
    }

    private static decimal? _KnownLootValue(
        IEnumerable<LootTallyLine> loot, LootKind lootKind, IReadOnlyDictionary<int, double> prices)
    {
        decimal[] values = [.. loot
            .Where(line => line.LootKind == lootKind && prices.ContainsKey(line.ItemTypeId))
            .Select(line => (decimal)prices[line.ItemTypeId] * line.Quantity.GetValueOrDefault())];
        return values.Length == 0 ? null : values.Sum();
    }
}
