using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;

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
        string rooms = HasRooms ? $"{detail.EnemyObservations.Max(observation => observation.RoomNumber)} rooms · " : string.Empty;
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

    // A room's window spans every run that had it: the pilots of one group can each have pressed NEW ROOM a moment apart.
    private void _ShowRooms(ActivityDetailDto detail)
    {
        EnemyRooms.Clear();
        if (detail.EnemyObservations.All(observation => observation.RoomNumber is null))
        {
            HasRooms = false;
            return;
        }

        // A toon of the group that never pressed NEW ROOM keeps its rows, under a header of their own after the rooms.
        foreach (IGrouping<int, RunEnemyObservationDto> room in detail.EnemyObservations
                     .GroupBy(observation => observation.RoomNumber ?? int.MaxValue)
                     .OrderBy(room => room.Key))
        {
            (DateTime Start, DateTime? End)[] windows = [.. detail.Runs
                .Select(run => (Run: run, Boundaries: RunRooms.Boundaries(detail.Parameters, run.RunId)))
                .Where(entry => entry.Boundaries.Count + 1 >= room.Key && entry.Boundaries.Count > 0)
                .Select(entry => (RunRooms.StartOf(entry.Boundaries, room.Key, entry.Run.StartedAtUtc),
                    RunRooms.EndOf(entry.Boundaries, room.Key, entry.Run.StoppedAtUtc)))];
            string windowText = windows.Length == 0 ? string.Empty : _WindowText(windows.Min(window => window.Start),
                windows.Any(window => window.End is null) ? null : windows.Max(window => window.End));
            List<ActivityEnemyRowViewModel> rows = [];
            foreach (RunEnemyObservationDto observation in room)
            {
                rows.Add(new ActivityEnemyRowViewModel(observation) { IsAlternate = rows.Count % 2 == 1 });
            }

            EnemyRooms.Add(new ActivityEnemyRoomViewModel(room.Key == int.MaxValue ? "NO ROOMS MARKED" : $"ROOM {room.Key}",
                room.Key == int.MaxValue ? string.Empty : windowText, room.Sum(observation => observation.Count), rows));
        }

        HasRooms = true;
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
