using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>An activity's summary and the runs behind it, assembled into the detail every screen reads — the Local
/// store's (<see cref="GetActivityDetailQuery"/>) and (ET-311) a server tab's, built from what the server handed back.</summary>
internal static class ActivityDetails
{
    /// <param name="runs">The activity's runs, each carrying its own loot captures.</param>
    public static ActivityDetailDto ToDto(ActivitySummary summary, IReadOnlyList<Run> runs,
        IReadOnlyList<RunBountyEntry> bountyEntries, IReadOnlyList<RunEnemyObservation> enemyObservations,
        IReadOnlyList<RunParameter> parameters, IReadOnlyList<RunMiningEntry> miningEntries,
        RunAttendanceDecision? attendance, long? fleetId, IReadOnlyDictionary<long, IskBreakdown> iskByCharacter) =>
        new(
            summary.Id, summary.GroupCode, summary.ActivityKind, summary.SiteName, summary.SignatureGroupSnapshot,
            summary.SiteTypeId, summary.SolarSystemId,
            summary.StartedAtUtc, summary.StoppedAtUtc, summary.DurationSeconds,
            summary.LootIskGained, summary.LootIskLost, summary.LootIskNet, summary.BountyIsk, summary.ExpectedPayoutIsk,
            summary.ParticipantCount, summary.PayoutEligibleCount,
            [.. runs.OrderBy(run => run.CharacterId).Select(_ToRunDto)],
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
                .Select(entry => new RunMiningEntryDto(entry.RunId, entry.OreType, entry.Units, entry.CriticalUnits,
                    entry.ResidueUnits, entry.FirstObservedAtUtc, entry.LastObservedAtUtc))],
            StoredIskBreakdown.Read(summary.IskContributions),
            attendance, fleetId, iskByCharacter);

    private static ActivityRunDetailDto _ToRunDto(Run run) => new(
        run.Id, run.CharacterId, run.Role, run.IsParticipant, run.IsPayoutEligible,
        run.StartedAtUtc, run.StoppedAtUtc, run.TimesCorrectedAtUtc,
        run.AgentId, run.MissionLevel, run.Signature, run.FitNameSnapshot,
        [.. run.LootCaptures.OrderBy(capture => capture.CapturedAtUtc).Select(RunLootCaptureMapper.ToDto)],
        run.SyncState, run.CharacterNameSnapshot, run.InSiteAtCompletion, run.FleetSizeAtStop,
        run.HomefrontPayoutTableVersion);
}
