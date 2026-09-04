using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

internal sealed class GetActivityDetailQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
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
            .Include(run => run.LootCaptures)
                .ThenInclude(capture => capture.Entries)
            .Include(run => run.BountyEntries)
            .Include(run => run.EnemyObservations)
            .Include(run => run.Parameters)
            .ToListAsync(cancellationToken);

        return Result<ActivityDetailDto>.Success(new ActivityDetailDto(
            summary.Id, summary.GroupCode, summary.ActivityKind, summary.SiteName, summary.SolarSystemId,
            summary.StartedAtUtc, summary.StoppedAtUtc, summary.DurationSeconds,
            summary.LootIskGained, summary.LootIskLost, summary.LootIskNet, summary.BountyIsk, summary.ExpectedPayoutIsk,
            [.. runs.OrderBy(run => run.CharacterId).Select(_ToRunDto)],
            [.. runs.SelectMany(run => run.BountyEntries)
                .OrderBy(entry => entry.OccurredAtUtc)
                .Select(entry => new RunBountyEntryDto(entry.RunId, entry.OccurredAtUtc, entry.Isk))],
            // Not grouped by EnemyTypeId: two runs in the same activity can each carry their own sighting of the
            // same type, and folding those into one row would silently overwrite whichever sighting lost the merge.
            [.. runs.SelectMany(run => run.EnemyObservations)
                .OrderBy(observation => observation.FirstObservedAtUtc)
                .Select(observation => new RunEnemyObservationDto(observation.RunId, observation.EnemyTypeId,
                    observation.EnemyName, observation.Count, observation.FirstObservedAtUtc, observation.LastObservedAtUtc))],
            [.. runs.SelectMany(run => run.Parameters)
                .OrderBy(parameter => parameter.ParameterKey).ThenBy(parameter => parameter.ObservedAtUtc)
                .Select(parameter => new RunParameterDto(parameter.RunId, parameter.ParameterKey, parameter.TypedValue,
                    parameter.Amount, parameter.ItemTypeId, parameter.BonusWindowSeconds, parameter.ObservedAtUtc))]));
    }

    private static ActivityRunDetailDto _ToRunDto(Run run) => new(
        run.Id, run.CharacterId, run.Role, run.IsPayoutEligible,
        [.. run.LootCaptures.OrderBy(capture => capture.CapturedAtUtc).Select(RunLootCaptureMapper.ToDto)]);
}
