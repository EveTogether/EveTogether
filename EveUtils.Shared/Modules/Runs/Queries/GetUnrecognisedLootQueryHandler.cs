using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetUnrecognisedLootQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetUnrecognisedLootQuery, Result<IReadOnlyList<UnrecognisedLootItemDto>>>
{
    private sealed record Sighting(
        string Name, long Quantity, UnrecognisedItemSource Source, DateTime FirstSeenAtUtc, Guid? RunId,
        DateTime? ResolvedAtUtc = null, int? ResolvedTypeId = null, decimal? ResolvedUnitPrice = null);

    public async Task<Result<IReadOnlyList<UnrecognisedLootItemDto>>> Handle(
        GetUnrecognisedLootQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<Sighting> sightings = await db.Set<UnrecognisedLootLine>()
            .AsNoTracking()
            .Where(line => line.Status == query.Status
                           && (line.RunLootCapture == null
                               || (!line.RunLootCapture.IsExcluded && !line.RunLootCapture.Run!.DeletedAtUtc.HasValue)))
            .Select(line => new Sighting(line.Name, line.Quantity, line.Source, line.FirstSeenAtUtc,
                line.RunLootCapture == null ? line.RunId : line.RunLootCapture.RunId,
                line.ResolvedAtUtc, line.ResolvedTypeId, line.ResolvedUnitPrice))
            .ToListAsync(cancellationToken);

        if (query.Status is UnrecognisedItemStatus.Open)
            sightings.AddRange(await _OpenMissionRewardsAsync(db, cancellationToken));

        // A mission's reward row is copied onto every own run of the group, and the mission paid it once.
        sightings =
        [
            .. sightings.Where(sighting => sighting.Source is not UnrecognisedItemSource.MissionReward),
            .. sightings.Where(sighting => sighting.Source is UnrecognisedItemSource.MissionReward)
                .DistinctBy(sighting => (sighting.Name, sighting.Quantity, sighting.FirstSeenAtUtc))
        ];

        return Result<IReadOnlyList<UnrecognisedLootItemDto>>.Success(
        [
            .. sightings
                .GroupBy(sighting => sighting.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => new UnrecognisedLootItemDto
                {
                    Name = group.First().Name,
                    TotalQuantity = group.Sum(sighting => sighting.Quantity),
                    RunCount = group.Select(sighting => sighting.RunId).OfType<Guid>().Distinct().Count(),
                    FirstSeenAtUtc = group.Min(sighting => sighting.FirstSeenAtUtc),
                    Sources = [.. group.Select(sighting => sighting.Source).Distinct().Order()],
                    ResolvedAtUtc = group.Max(sighting => sighting.ResolvedAtUtc),
                    ResolvedTypeId = group.Select(sighting => sighting.ResolvedTypeId).FirstOrDefault(typeId => typeId is not null),
                    ResolvedUnitPrice = group.Select(sighting => sighting.ResolvedUnitPrice).FirstOrDefault(price => price is not null)
                })
                .OrderBy(item => item.FirstSeenAtUtc)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        ]);
    }

    private static async Task<IReadOnlyList<Sighting>> _OpenMissionRewardsAsync(ClientDbContext db, CancellationToken cancellationToken)
    {
        List<RunParameter> untyped = await db.Set<RunParameter>()
            .AsNoTracking()
            .Where(parameter => parameter.ParameterKey == RunParameterKey.Item && parameter.ItemTypeId == null
                                && !parameter.Run!.DeletedAtUtc.HasValue)
            .ToListAsync(cancellationToken);
        return
        [
            .. untyped
                .Where(parameter => MissionRewardItems.NameOf(parameter) is not null)
                .Select(parameter => new Sighting(MissionRewardItems.NameOf(parameter) ?? string.Empty,
                    (long)parameter.Amount.GetValueOrDefault(1), UnrecognisedItemSource.MissionReward,
                    parameter.ObservedAtUtc, parameter.RunId))
        ];
    }
}
