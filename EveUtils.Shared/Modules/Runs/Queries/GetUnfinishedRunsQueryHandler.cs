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
internal sealed class GetUnfinishedRunsQueryHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices, ISdeAccessor sde)
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
            // bounty and mining entries for whenever a future run persists them before SAVE too.
            .Include(run => run.LootCaptures).ThenInclude(capture => capture.Entries)
            .Include(run => run.BountyEntries)
            .Include(run => run.MiningEntries)
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
        // CONSUMABLES prices through the same cache, keyed by each run's own resolved filament type (ET-249).
        List<int> filamentTypeIds = [.. runs
            .Select(run => RunIskFactsReader.FilamentTypeId(run.Parameters))
            .OfType<int>()
            .Distinct()];
        // MINING prices through the same cache, keyed by each ore's own resolved type (ET-229) — Mutanite's fixed
        // NPC price never needs it (MiningValuation).
        List<int> oreTypeIds = sde.IsAvailable
            ? [.. runs.SelectMany(run => run.MiningEntries)
                .Select(entry => sde.TryGetTypeId(entry.OreType, out int typeId) ? (int?)typeId : null)
                .OfType<int>()
                .Distinct()]
            : [];
        List<int> priceTypeIds = [.. lootTypeIds.Concat(filamentTypeIds).Concat(oreTypeIds).Distinct()];
        IReadOnlyDictionary<int, double> prices = priceTypeIds.Count == 0
            ? new Dictionary<int, double>()
            : await marketPrices.GetAveragePricesAsync(priceTypeIds, cancellationToken);

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
    // Unknown only when loot is the sole reason nothing can be said: bounty and rewards are read straight off storage,
    // never priced, so either one being there already makes the total a real (if possibly loot-incomplete) figure.
    private (decimal Total, bool Unknown) _TotalIsk(Run run, IReadOnlyDictionary<int, double> prices)
    {
        IskBreakdown isk = IskContributors.Breakdown([RunIskFactsReader.From(run, run.Parameters, prices, sde)], DateTime.UtcNow);
        return (isk.Total, isk.IsUnvalued);
    }
}
