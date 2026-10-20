using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetActivityYieldQueryHandler(IDbContextFactory<ClientDbContext> contextFactory, ISdeAccessor sde,
    IMarketPriceRepository marketPrices, IBlueprintAppraisalService blueprints)
    : IQueryHandler<GetActivityYieldQuery, Result<ActivityYieldDto?>>
{
    public async Task<Result<ActivityYieldDto?>> Handle(GetActivityYieldQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IReadOnlyList<ActivityEarningsSample> samples = await LocalEarningsSamples.ReadAsync(db, sde, DateTime.MinValue,
            DateTime.UtcNow, cancellationToken, [query.ActivityRef]);
        if (samples.FirstOrDefault(sample => sample.ActivityRef == query.ActivityRef) is not { } activity)
            return Result<ActivityYieldDto?>.Success(null);

        EarningsSample sample = activity.Sample;
        Func<int, decimal?> unitPrice = await TodaysLootPrices.ForAsync(marketPrices, blueprints, sde,
            [.. sample.LootGained.Concat(sample.LootLost).Concat(sample.Mined).Select(line => line.TypeId).Distinct()],
            cancellationToken);
        return Result<ActivityYieldDto?>.Success(new ActivityYieldDto
        {
            Key = activity.Key,
            YieldIsk = EarningsStatistics.YieldOf(sample, unitPrice),
            CompletedAtUtc = sample.CompletedAtUtc,
            IsCounted = EarningsStatistics.IsCounted(sample, EarningsPolicy.For(activity.Key.Kind)),
            TopDrop = _TopDrop(sample, unitPrice)
        });
    }

    private ActivityTopDropDto? _TopDrop(EarningsSample sample, Func<int, decimal?> unitPrice)
    {
        Func<int, LootRarityClass> rarity = LootRarity.Of(sde);
        return sample.LootGained
            .Select(line => (Line: line, Value: unitPrice(line.TypeId).GetValueOrDefault() * line.Quantity))
            .Where(entry => entry.Value > 0)
            .OrderByDescending(entry => entry.Value)
            .Select(entry => new ActivityTopDropDto
            {
                TypeId = entry.Line.TypeId,
                Name = sde.GetType(entry.Line.TypeId)?.Name ?? $"Type {entry.Line.TypeId}",
                Quantity = entry.Line.Quantity,
                ValueIsk = entry.Value,
                Rarity = rarity(entry.Line.TypeId)
            })
            .FirstOrDefault();
    }
}
