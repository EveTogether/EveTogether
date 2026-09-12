using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
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
            .ToListAsync(cancellationToken);
        List<Guid> runIds = [.. runs.Select(run => run.Id)];

        // Four collections queried separately by RunId rather than as parallel Includes on Run: EF folds sibling
        // collection includes into one join and multiplies them against each other — a six-run fleet activity with
        // twenty loot lines, five bounties, ten sightings and three parameters per run would pull thousands of rows,
        // each loot line repeated once per bounty/sighting/parameter combination, for what is a few hundred rows.
        ILookup<Guid, RunLootCapture> lootByRun = (await db.Set<RunLootCapture>()
            .AsNoTracking()
            .Where(capture => runIds.Contains(capture.RunId))
            .Include(capture => capture.Entries)
            .ToListAsync(cancellationToken)).ToLookup(capture => capture.RunId);
        List<RunBountyEntry> bountyEntries = await db.Set<RunBountyEntry>()
            .AsNoTracking().Where(entry => runIds.Contains(entry.RunId)).ToListAsync(cancellationToken);
        List<RunEnemyObservation> enemyObservations = await db.Set<RunEnemyObservation>()
            .AsNoTracking().Where(observation => runIds.Contains(observation.RunId)).ToListAsync(cancellationToken);
        List<RunParameter> parameters = await db.Set<RunParameter>()
            .AsNoTracking().Where(parameter => runIds.Contains(parameter.RunId)).ToListAsync(cancellationToken);
        List<RunMiningEntry> miningEntries = await db.Set<RunMiningEntry>()
            .AsNoTracking().Where(entry => runIds.Contains(entry.RunId)).ToListAsync(cancellationToken);

        // Every run of the group carries the same attendance decision (ET-230); the newest one is the one that counts,
        // and only its list is read.
        RunAttendanceDecision? attendance = null;
        if (runs.Where(run => run.AttendanceSetAtUtc.HasValue).MaxBy(run => run.AttendanceSetAtUtc) is { } decided)
        {
            foreach (RunAttendanceEntry entry in await db.Set<RunAttendanceEntry>()
                         .AsNoTracking().Where(entry => entry.RunId == decided.Id).ToListAsync(cancellationToken))
                decided.AttendanceEntries.Add(entry);
            attendance = RunAttendanceDecision.Of(decided);
        }

        long? fleetId = summary.GroupCode is null
            ? null
            : await db.Set<RunGroupOrigin>().AsNoTracking()
                .Where(origin => origin.GroupCode == summary.GroupCode)
                .Select(origin => (long?)origin.FleetId)
                .FirstOrDefaultAsync(cancellationToken);

        return Result<ActivityDetailDto>.Success(new ActivityDetailDto(
            summary.Id, summary.GroupCode, summary.ActivityKind, summary.SiteName, summary.SignatureGroupSnapshot,
            summary.SiteTypeId, summary.SolarSystemId,
            summary.StartedAtUtc, summary.StoppedAtUtc, summary.DurationSeconds,
            summary.LootIskGained, summary.LootIskLost, summary.LootIskNet, summary.BountyIsk, summary.ExpectedPayoutIsk,
            summary.ParticipantCount, summary.PayoutEligibleCount,
            [.. runs.OrderBy(run => run.CharacterId).Select(run => _ToRunDto(run, lootByRun[run.Id]))],
            [.. bountyEntries.OrderBy(entry => entry.OccurredAtUtc)
                .Select(entry => new RunBountyEntryDto(entry.RunId, entry.OccurredAtUtc, entry.Isk))],
            // Not grouped by EnemyTypeId: two runs in the same activity can each carry their own sighting of the
            // same type, and folding those into one row would silently overwrite whichever sighting lost the merge.
            [.. enemyObservations.OrderBy(observation => observation.FirstObservedAtUtc)
                .Select(observation => new RunEnemyObservationDto(observation.RunId, observation.EnemyTypeId,
                    observation.EnemyName, observation.Count, observation.FirstObservedAtUtc, observation.LastObservedAtUtc))],
            [.. parameters.OrderBy(parameter => parameter.ParameterKey).ThenBy(parameter => parameter.ObservedAtUtc)
                .Select(parameter => new RunParameterDto(parameter.RunId, parameter.ParameterKey, parameter.TypedValue,
                    parameter.Amount, parameter.ItemTypeId, parameter.BonusWindowSeconds, parameter.ObservedAtUtc))],
            [.. miningEntries.OrderByDescending(entry => entry.Units)
                .Select(entry => new RunMiningEntryDto(entry.RunId, entry.OreType, entry.Units, entry.CriticalUnits, entry.ResidueUnits))],
            StoredIskBreakdown.Read(summary.IskContributions),
            attendance, fleetId));
    }

    private static ActivityRunDetailDto _ToRunDto(Run run, IEnumerable<RunLootCapture> lootCaptures) => new(
        run.Id, run.CharacterId, run.Role, run.IsParticipant, run.IsPayoutEligible,
        run.StartedAtUtc, run.StoppedAtUtc, run.TimesCorrectedAtUtc,
        run.AgentId, run.MissionLevel, run.Signature, run.FitNameSnapshot,
        [.. lootCaptures.OrderBy(capture => capture.CapturedAtUtc).Select(RunLootCaptureMapper.ToDto)],
        run.SyncState, run.CharacterNameSnapshot, run.InSiteAtCompletion, run.FleetSizeAtStop,
        run.HomefrontPayoutTableVersion);
}
