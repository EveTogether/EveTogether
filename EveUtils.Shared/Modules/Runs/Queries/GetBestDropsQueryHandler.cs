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
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetBestDropsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices)
    : IQueryHandler<GetBestDropsQuery, Result<IReadOnlyList<BestDropDto>>>
{
    public async Task<Result<IReadOnlyList<BestDropDto>>> Handle(GetBestDropsQuery query, CancellationToken cancellationToken = default)
    {
        if (query.CharacterIds.Count == 0)
            return Result<IReadOnlyList<BestDropDto>>.Success([]);

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<Run> runs = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => query.CharacterIds.Contains(run.CharacterId) && run.State == RunState.Saved
                          && !run.DeletedAtUtc.HasValue && run.StartedAtUtc >= query.FromUtc)
            .Include(run => run.LootCaptures).ThenInclude(capture => capture.Entries)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        // Loot counts for the character whose run holds the capture, without a split (ET-296) — so summing the runs
        // is the own total, the same as the earnings on the tiles.
        Dictionary<int, long> quantities = [];
        foreach (LootTallyLine line in runs.SelectMany(run => LootTally.Count(RunIskFactsReader.Tally(run)))
                     .Where(line => line.LootKind == LootKind.Gained))
            quantities[line.ItemTypeId] = quantities.GetValueOrDefault(line.ItemTypeId) + (line.Quantity ?? 1);
        if (quantities.Count == 0)
            return Result<IReadOnlyList<BestDropDto>>.Success([]);

        Dictionary<int, string> names = runs
            .SelectMany(run => run.LootCaptures)
            .SelectMany(capture => capture.Entries)
            .GroupBy(entry => entry.ItemTypeId)
            .ToDictionary(group => group.Key, group => group.First().Name);
        IReadOnlyDictionary<int, double> prices = await marketPrices.GetAveragePricesAsync([.. quantities.Keys], cancellationToken);

        return Result<IReadOnlyList<BestDropDto>>.Success(
        [
            .. quantities
                .Where(item => prices.ContainsKey(item.Key))
                .Select(item => new BestDropDto(item.Key, names.GetValueOrDefault(item.Key, $"#{item.Key}"), item.Value,
                    (decimal)prices[item.Key] * item.Value))
                .OrderByDescending(drop => drop.Value)
                .Take(query.Take)
        ]);
    }
}
