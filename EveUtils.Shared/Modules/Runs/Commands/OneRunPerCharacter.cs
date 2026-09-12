using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// I7 (ET-274): at most one live run per character per group. The client store holds it with a unique filtered index
/// (<see cref="RunsModule.ConfigureClientModel"/>); this is what every path that files a run under a group asks first
/// — a start, a backfill, a relink — and what folds a second run into the first where two runs of one character have
/// to become one. The migration that brought the index in (EnforceOneRunPerCharacterInGroup) folds the duplicates that
/// were already stored by the same rules, in SQL.
/// </summary>
internal static class OneRunPerCharacter
{
    /// <summary>The live run <paramref name="characterId"/> already has in <paramref name="groupCode"/>, tracked.</summary>
    public static Task<Run?> FindAsync(ClientDbContext db, string groupCode, long characterId, CancellationToken cancellationToken) =>
        db.Set<Run>().FirstOrDefaultAsync(run => run.GroupCode == groupCode && run.CharacterId == characterId
                                                 && !run.DeletedAtUtc.HasValue, cancellationToken);

    /// <summary>Everything <see cref="Merge"/> moves, loaded with the run.</summary>
    public static IQueryable<Run> WithEverything(this IQueryable<Run> runs) => runs
        .Include(run => run.BountyEntries)
        .Include(run => run.LootCaptures)
        .Include(run => run.Parameters)
        .Include(run => run.EnemyObservations)
        .Include(run => run.MiningEntries)
        .Include(run => run.AttendanceEntries);

    /// <summary>
    /// Fold <paramref name="duplicate"/> into <paramref name="survivor"/> — two runs of one character in one group,
    /// both loaded <see cref="WithEverything"/>. Every fact the survivor lacks moves over, one it already has is never
    /// counted twice: a bounty line by its moment and amount, a loot copy by its content, a parameter by its key and
    /// item, an enemy and an ore by their type. The newer attendance list wins, an outcome is never erased (I4), and
    /// the duplicate is deleted the way a delete is published, so a copy already on a server goes too.
    /// </summary>
    public static void Merge(ClientDbContext db, Run survivor, Run duplicate, DateTime nowUtc)
    {
        foreach (RunBountyEntry line in duplicate.BountyEntries.ToList()
                     .Where(line => !survivor.BountyEntries.Any(kept => kept.OccurredAtUtc == line.OccurredAtUtc && kept.Isk == line.Isk)))
            _Move(line, survivor.BountyEntries, duplicate.BountyEntries, survivor.Id, (entry, runId) => entry.RunId = runId);
        foreach (RunLootCapture capture in duplicate.LootCaptures.ToList()
                     .Where(capture => capture.ContentHash is null || survivor.LootCaptures.All(kept => kept.ContentHash != capture.ContentHash)))
            _Move(capture, survivor.LootCaptures, duplicate.LootCaptures, survivor.Id, (entry, runId) => entry.RunId = runId);
        foreach (RunParameter parameter in duplicate.Parameters.ToList()
                     .Where(parameter => !survivor.Parameters.Any(kept => kept.ParameterKey == parameter.ParameterKey && kept.ItemTypeId == parameter.ItemTypeId)))
            _Move(parameter, survivor.Parameters, duplicate.Parameters, survivor.Id, (entry, runId) => entry.RunId = runId);
        _MergeEnemies(survivor, duplicate);
        _MergeMining(survivor, duplicate);
        _MergeAttendance(db, survivor, duplicate);
        _MergeRun(survivor, duplicate);

        survivor.Revision++;
        if (survivor.SyncState is RunSyncState.Synced)
            survivor.SyncState = RunSyncState.Outdated;
        duplicate.DeletedAtUtc = nowUtc;
        duplicate.Revision++;
        if (duplicate.SyncState is not RunSyncState.Local)
            duplicate.SyncState = RunSyncState.Pending;
    }

    private static void _Move<T>(T child, ICollection<T> to, ICollection<T> from, Guid runId, Action<T, Guid> relink)
    {
        from.Remove(child);
        relink(child, runId);
        to.Add(child);
    }

    // A count is typed by hand and only ever grows, so the larger one is the one somebody typed.
    private static void _MergeEnemies(Run survivor, Run duplicate)
    {
        foreach (RunEnemyObservation seen in duplicate.EnemyObservations.ToList())
        {
            if (survivor.EnemyObservations.FirstOrDefault(kept => kept.EnemyTypeId == seen.EnemyTypeId && kept.EnemyName == seen.EnemyName)
                is not { } kept)
            {
                _Move(seen, survivor.EnemyObservations, duplicate.EnemyObservations, survivor.Id, (entry, runId) => entry.RunId = runId);
                continue;
            }

            kept.Count = Math.Max(kept.Count, seen.Count);
            kept.FirstObservedAtUtc = kept.FirstObservedAtUtc <= seen.FirstObservedAtUtc ? kept.FirstObservedAtUtc : seen.FirstObservedAtUtc;
            kept.LastObservedAtUtc = kept.LastObservedAtUtc >= seen.LastObservedAtUtc ? kept.LastObservedAtUtc : seen.LastObservedAtUtc;
        }
    }

    // One row per ore on a run, grown cycle by cycle: the fuller of two copies holds every cycle the other one does.
    private static void _MergeMining(Run survivor, Run duplicate)
    {
        foreach (RunMiningEntry mined in duplicate.MiningEntries.ToList())
        {
            if (survivor.MiningEntries.FirstOrDefault(kept => kept.OreType == mined.OreType) is not { } kept)
            {
                _Move(mined, survivor.MiningEntries, duplicate.MiningEntries, survivor.Id, (entry, runId) => entry.RunId = runId);
                continue;
            }

            if (mined.Units <= kept.Units)
                continue;
            kept.Units = mined.Units;
            kept.CriticalUnits = mined.CriticalUnits;
            kept.ResidueUnits = mined.ResidueUnits;
            kept.FirstObservedAtUtc = mined.FirstObservedAtUtc;
            kept.LastObservedAtUtc = mined.LastObservedAtUtc;
        }
    }

    private static void _MergeAttendance(ClientDbContext db, Run survivor, Run duplicate)
    {
        bool isNewer = duplicate.AttendanceSetAtUtc is { } duplicateAt
                       && (survivor.AttendanceSetAtUtc is not { } survivorAt || duplicateAt > survivorAt);
        if (isNewer)
        {
            db.Set<RunAttendanceEntry>().RemoveRange(survivor.AttendanceEntries);
            survivor.AttendanceEntries.Clear();
            foreach (RunAttendanceEntry entry in duplicate.AttendanceEntries.ToList())
                _Move(entry, survivor.AttendanceEntries, duplicate.AttendanceEntries, survivor.Id, (moved, runId) => moved.RunId = runId);
            survivor.InSiteAtCompletion = duplicate.InSiteAtCompletion;
            survivor.AttendanceCount = duplicate.AttendanceCount;
            survivor.AttendanceNotOnRosterCount = duplicate.AttendanceNotOnRosterCount;
            survivor.AttendanceSource = duplicate.AttendanceSource;
            survivor.AttendanceSetByCharacterId = duplicate.AttendanceSetByCharacterId;
            survivor.AttendanceSetAtUtc = duplicate.AttendanceSetAtUtc;
            survivor.HomefrontPayoutTableVersion = duplicate.HomefrontPayoutTableVersion ?? survivor.HomefrontPayoutTableVersion;
        }

        // Only another outcome replaces an outcome (RunAttendanceDecision.KeepingOutcomeOf): the newer list's, when it
        // says one, and otherwise whichever copy said one at all.
        bool hasOutcome = duplicate.HomefrontOutcome is not null || duplicate.HomefrontCompletedWaveCount is not null;
        bool survivorHasOutcome = survivor.HomefrontOutcome is not null || survivor.HomefrontCompletedWaveCount is not null;
        if (hasOutcome && (isNewer || !survivorHasOutcome))
        {
            survivor.HomefrontOutcome = duplicate.HomefrontOutcome;
            survivor.HomefrontCompletedWaveCount = duplicate.HomefrontCompletedWaveCount;
            survivor.HomefrontOutcomeFromGameLog = duplicate.HomefrontOutcomeFromGameLog;
        }
    }

    // What one copy knows and the other does not; and the further state of the two, since a group is stopped and saved
    // as a whole and the copy left behind only missed the last step.
    private static void _MergeRun(Run survivor, Run duplicate)
    {
        survivor.FitContentHash ??= duplicate.FitContentHash;
        survivor.FitNameSnapshot ??= duplicate.FitNameSnapshot;
        survivor.CharacterNameSnapshot ??= duplicate.CharacterNameSnapshot;
        survivor.SignatureGroupSnapshot ??= duplicate.SignatureGroupSnapshot;
        survivor.SolarSystemId ??= duplicate.SolarSystemId;
        survivor.AgentId ??= duplicate.AgentId;
        survivor.MissionLevel ??= duplicate.MissionLevel;
        survivor.LootStrategy ??= duplicate.LootStrategy;
        survivor.FleetSizeAtStop ??= duplicate.FleetSizeAtStop;
        if (survivor.SiteTypeId == 0 && duplicate.SiteTypeId != 0)
        {
            survivor.SiteTypeId = duplicate.SiteTypeId;
            survivor.SiteTypeSource = duplicate.SiteTypeSource;
        }

        if (duplicate.State <= survivor.State)
            return;
        survivor.State = duplicate.State;
        survivor.StoppedAtUtc ??= duplicate.StoppedAtUtc;
        survivor.SavedAtUtc ??= duplicate.SavedAtUtc;
        survivor.AutoSavedAtUtc ??= duplicate.AutoSavedAtUtc;
    }
}
