using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes.Dtos;
using EveUtils.Shared.Modules.RunOutcomes.Enums;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetOutcomeSharesQueryHandler(IDbContextFactory<ClientDbContext> contextFactory, ISdeAccessor sde)
    : IQueryHandler<GetOutcomeSharesQuery, Result<IReadOnlyList<ActivityOutcomeShareDto>>>
{
    public async Task<Result<IReadOnlyList<ActivityOutcomeShareDto>>> Handle(GetOutcomeSharesQuery query,
        CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IReadOnlyList<ActivityEarningsSample> samples = await LocalEarningsSamples.ReadAsync(db, sde, query.SinceUtc,
            DateTime.UtcNow, cancellationToken, query.ActivityRefs);
        Dictionary<string, ActivityEarningsSample> sampleByRef = samples.ToDictionary(sample => sample.ActivityRef, StringComparer.Ordinal);
        string[] activityRefs = [.. query.ActivityRefs ?? [.. sampleByRef.Keys]];
        if (activityRefs.Length == 0)
            return Result<IReadOnlyList<ActivityOutcomeShareDto>>.Success([]);

        // Every run of the activity, deleted ones too: a deletion bumps the run's revision, and the tombstone has to
        // go out above what was shared.
        var runs = (await ActivityRefs.Of(db.Set<Run>().AsNoTracking(), activityRefs)
                .Select(run => new { run.Id, run.GroupCode, run.CharacterId, run.Revision, run.StartedAtUtc, run.SyncServerAddress })
                .ToListAsync(cancellationToken))
            .GroupBy(run => run.GroupCode ?? run.Id.ToString(), StringComparer.Ordinal)
            .ToDictionary(activity => activity.Key, activity => activity.OrderBy(run => run.StartedAtUtc).ThenBy(run => run.Id).ToList(),
                StringComparer.Ordinal);
        Dictionary<string, string?> originServers = await db.Set<RunGroupOrigin>().AsNoTracking()
            .Where(origin => activityRefs.Contains(origin.GroupCode))
            .ToDictionaryAsync(origin => origin.GroupCode, origin => origin.ServerAddress, StringComparer.Ordinal, cancellationToken);

        List<ActivityOutcomeShareDto> shares = [];
        foreach (string activityRef in activityRefs)
        {
            if (!runs.TryGetValue(activityRef, out var activityRuns))
                continue;

            sampleByRef.TryGetValue(activityRef, out ActivityEarningsSample? sample);
            shares.Add(new ActivityOutcomeShareDto
            {
                ActivityRef = activityRef,
                CharacterId = sample?.CharacterId ?? activityRuns[0].CharacterId,
                Revision = activityRuns.Sum(run => (long)run.Revision),
                PreferredServer = originServers.GetValueOrDefault(activityRef)
                                  ?? activityRuns.Select(run => run.SyncServerAddress).FirstOrDefault(address => address is not null),
                Outcome = sample is null ? null : _Outcome(sample)
            });
        }

        return Result<IReadOnlyList<ActivityOutcomeShareDto>>.Success(shares);
    }

    private static RunOutcomeShareDto _Outcome(ActivityEarningsSample activity)
    {
        EarningsSample sample = activity.Sample;
        return new RunOutcomeShareDto
        {
            Key = activity.Key,
            CompletedOn = DateOnly.FromDateTime(sample.CompletedAtUtc),
            DurationSeconds = sample.DurationSeconds,
            FixedIsk = sample.FixedIsk,
            FleetSize = sample.FleetSize,
            LootBasis = sample.LootBasis,
            OpenUnrecognisedLootCount = sample.OpenUnrecognisedLootCount,
            LootLines =
            [
                .. _Lines(sample.LootGained, RunOutcomeLootKind.Gained),
                .. _Lines(sample.LootLost, RunOutcomeLootKind.Lost),
                .. _Lines(sample.Mined, RunOutcomeLootKind.Mined)
            ]
        };
    }

    private static IEnumerable<RunOutcomeLootLineDto> _Lines(IEnumerable<EarningsLootLine> lines, RunOutcomeLootKind kind) =>
        lines.Select(line => new RunOutcomeLootLineDto { Kind = kind, TypeId = line.TypeId, Quantity = line.Quantity });
}
