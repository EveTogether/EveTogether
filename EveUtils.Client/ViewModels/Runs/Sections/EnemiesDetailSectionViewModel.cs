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

    private void _ShowRooms(ActivityDetailDto detail)
    {
        EnemyRooms.Clear();
        RunRoomSet? rooms = RunRoomSet.Of(detail);
        HasRooms = rooms is not null;
        if (rooms is null)
        {
            return;
        }

        foreach ((int room, List<RunEnemyObservationDto> merged) in rooms.EnemiesByRoom(detail))
        {
            List<ActivityEnemyRowViewModel> rows = [];
            foreach (RunEnemyObservationDto observation in merged)
            {
                rows.Add(new ActivityEnemyRowViewModel(observation) { IsAlternate = rows.Count % 2 == 1 });
            }

            EnemyRooms.Add(new ActivityEnemyRoomViewModel($"ROOM {room}",
                _WindowText(RunRooms.StartOf(rooms.Boundaries, room, rooms.Run.StartedAtUtc),
                    RunRooms.EndOf(rooms.Boundaries, room, rooms.Run.StoppedAtUtc)),
                merged.Sum(observation => observation.Count), rows,
                RoomSourceViewModel.Of(_CertaintyOf(detail, rooms.Run.RunId, room))));
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
