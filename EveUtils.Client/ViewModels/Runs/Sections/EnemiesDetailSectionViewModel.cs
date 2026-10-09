using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>ENEMIES on the detail screen: what was counted, per character and per type.</summary>
public sealed partial class EnemiesDetailSectionViewModel() : RunDetailSection(RunSectionId.Enemies, "ENEMIES")
{
    public ObservableCollection<ActivityEnemyRowViewModel> EnemyRows { get; } = [];

    public ObservableCollection<ActivityEnemyCharacterRowViewModel> EnemyCharacterRows { get; } = [];

    /// <summary>The same rows per room, oldest room first, once a run of the activity has rooms (ET-240). Empty for
    /// every other activity, and <see cref="EnemyRows"/> is shown as it always was.</summary>
    public ObservableCollection<ActivityEnemyRoomViewModel> EnemyRooms { get; } = [];

    [ObservableProperty] private bool _hasRooms;

    [ObservableProperty] private string? _enemiesEmptyText;

    /// <summary>Whether any character logged a counted sighting at all — the same "no figure for nobody" rule the
    /// bounty breakdown follows, so the per-character breakdown and its total do not show a false zero (ET-210 review
    /// finding, 2026-09-09, round 4).</summary>
    [ObservableProperty] private bool _hasEnemyFigures;

    [ObservableProperty] private string _enemyTotalCountText = string.Empty;

    public override bool HasContent => EnemyRows.Count > 0;

    public override void Apply(RunDetailSectionInput input)
    {
        ActivityDetailDto detail = input.Detail;
        EnemyRows.Clear();
        foreach (RunEnemyObservationDto observation in detail.EnemyObservations)
            EnemyRows.Add(new ActivityEnemyRowViewModel(observation) { IsAlternate = EnemyRows.Count % 2 == 1 });

        // An empty list says no enemy observations were recorded, not that no combat happened.
        EnemiesEmptyText = EnemyRows.Count > 0
            ? null
            : "No enemy observations were recorded for this activity.";
        int enemyTypeCount = detail.EnemyObservations.Select(observation => observation.EnemyTypeId).Distinct().Count();
        int countedEnemyCount = detail.EnemyObservations.Sum(observation => observation.Count);
        _ShowRooms(detail);
        string rooms = HasRooms ? $"{EnemyRooms.Count} rooms · " : string.Empty;
        HeaderSummary = rooms + (countedEnemyCount > 0
            ? $"{countedEnemyCount} counted · {enemyTypeCount} types"
            : enemyTypeCount > 0 ? $"none counted · {enemyTypeCount} types" : "none counted");

        // One row per character, summed across every type they logged — the same breakdown BOUNTY already gives
        // (ET-210 review finding, 2026-09-09, round 4: Jithran chose per-character tracking with a group total,
        // not one shared tally). Largest contribution first, same ordering rule as the bounty breakdown. Only counted
        // sightings take part: a character who only saw enemies has no figure, not a figure of zero.
        EnemyCharacterRows.Clear();
        Dictionary<Guid, long> characterByRun = detail.Runs.ToDictionary(run => run.RunId, run => run.CharacterId);
        foreach (IGrouping<long, RunEnemyObservationDto> group in detail.EnemyObservations
                     .Where(observation => observation.Count > 0 && characterByRun.ContainsKey(observation.RunId))
                     .GroupBy(observation => characterByRun[observation.RunId])
                     .OrderByDescending(group => group.Sum(observation => observation.Count)))
            EnemyCharacterRows.Add(new ActivityEnemyCharacterRowViewModel(
                group.Key, group.Sum(observation => observation.Count), input.NameOf)
            {
                IsAlternate = EnemyCharacterRows.Count % 2 == 1
            });

        HasEnemyFigures = countedEnemyCount > 0;
        EnemyTotalCountText = countedEnemyCount == 1 ? "1 enemy" : $"{countedEnemyCount} enemies";
    }

    // One set of rooms for the whole group, so every client of the fleet draws the same ones (ET-494): the commander's
    // run decides when it has rooms, else the fullest list, the earliest run on a tie.
    private void _ShowRooms(ActivityDetailDto detail)
    {
        EnemyRooms.Clear();
        List<(ActivityRunDetailDto Run, IReadOnlyList<DateTime> Boundaries)> withRooms = [.. detail.Runs
            .OrderBy(run => run.StartedAtUtc).ThenBy(run => run.RunId)
            .Select(run => (Run: run, Boundaries: RunRooms.Boundaries(detail.Parameters, run.RunId)))
            .Where(entry => entry.Boundaries.Count > 0)];
        HasRooms = withRooms.Count > 0;
        if (!HasRooms)
        {
            return;
        }

        (ActivityRunDetailDto Run, IReadOnlyList<DateTime> Boundaries) rooms =
            withRooms.FirstOrDefault(entry => entry.Run.Role is RunRole.FleetCommander) is { Run: not null } commanders
                ? commanders
                : withRooms.MaxBy(entry => entry.Boundaries.Count);

        // Every pilot's row lands in a room by its first sighting, and one type is one row per room across the pilots.
        foreach (IGrouping<int, RunEnemyObservationDto> room in detail.EnemyObservations
                     .GroupBy(observation => RunRooms.RoomOf(rooms.Boundaries, observation.FirstObservedAtUtc) ?? 1)
                     .OrderBy(room => room.Key))
        {
            List<ActivityEnemyRowViewModel> rows = [];
            int counted = 0;
            foreach (IGrouping<int, RunEnemyObservationDto> type in room.GroupBy(observation => observation.EnemyTypeId))
            {
                // The pilots saw the same spawn, so the count is the largest anyone typed, not a sum.
                RunEnemyObservationDto merged = type.First() with
                {
                    Count = type.Max(observation => observation.Count),
                    FirstObservedAtUtc = type.Min(observation => observation.FirstObservedAtUtc),
                    LastObservedAtUtc = type.Max(observation => observation.LastObservedAtUtc)
                };
                counted += merged.Count;
                rows.Add(new ActivityEnemyRowViewModel(merged) { IsAlternate = rows.Count % 2 == 1 });
            }

            EnemyRooms.Add(new ActivityEnemyRoomViewModel($"ROOM {room.Key}",
                _WindowText(RunRooms.StartOf(rooms.Boundaries, room.Key, rooms.Run.StartedAtUtc),
                    RunRooms.EndOf(rooms.Boundaries, room.Key, rooms.Run.StoppedAtUtc)),
                counted, rows, RoomSourceViewModel.Of(_CertaintyOf(detail, rooms.Run.RunId, room.Key))));
        }
    }

    /// <summary>A room the detector opened on a run with no boundary of the pilot's says so, and how sure (ET-368).</summary>
    private static RoomCertainty? _CertaintyOf(ActivityDetailDto detail, Guid runId, int room)
    {
        List<RunParameterDto> rows = [.. detail.Parameters
            .Where(parameter => parameter.RunId == runId
                                && parameter.ParameterKey is RunParameterKey.RoomStarted or RunParameterKey.RoomDetected)
            .OrderBy(parameter => parameter.ObservedAtUtc)];
        string? certainty = room > 1 && rows.Count >= room - 1 && rows.All(row => row.ParameterKey == RunParameterKey.RoomDetected)
            ? rows[room - 2].TypedValue
            : null;
        return Enum.TryParse(certainty, ignoreCase: true, out RoomCertainty parsed) ? parsed : null;
    }

    private static string _WindowText(DateTime start, DateTime? end)
    {
        if (end is not { } ended)
        {
            return $"{start.ToLocalTime():HH:mm:ss} – ?";
        }

        TimeSpan length = ended - start;
        return $"{start.ToLocalTime():HH:mm:ss} – {ended.ToLocalTime():HH:mm:ss} · {(int)length.TotalMinutes}m {length.Seconds:00}s";
    }
}
