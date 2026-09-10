using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>ENEMIES on the detail screen: what was counted, per character and per type.</summary>
public sealed partial class EnemiesDetailSectionViewModel() : RunDetailSection(RunSectionId.Enemies, "ENEMIES")
{
    public ObservableCollection<ActivityEnemyRowViewModel> EnemyRows { get; } = [];

    public ObservableCollection<ActivityEnemyCharacterRowViewModel> EnemyCharacterRows { get; } = [];

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
            EnemyRows.Add(new ActivityEnemyRowViewModel(observation));

        // An empty list says no enemy observations were recorded, not that no combat happened.
        EnemiesEmptyText = EnemyRows.Count > 0
            ? null
            : "No enemy observations were recorded for this activity.";
        int enemyTypeCount = detail.EnemyObservations.Select(observation => observation.EnemyTypeId).Distinct().Count();
        int countedEnemyCount = detail.EnemyObservations.Sum(observation => observation.Count);
        HeaderSummary = countedEnemyCount > 0
            ? $"{countedEnemyCount} counted · {enemyTypeCount} types"
            : enemyTypeCount > 0 ? $"none counted · {enemyTypeCount} types" : "none counted";

        // One row per character, summed across every type they logged — the same breakdown BOUNTY already gives
        // (ET-210 review finding, 2026-09-09, round 4: Jithran chose per-character tracking with a group total,
        // not one shared tally). Largest contribution first, same ordering rule as the bounty breakdown.
        EnemyCharacterRows.Clear();
        Dictionary<Guid, long> characterByRun = detail.Runs.ToDictionary(run => run.RunId, run => run.CharacterId);
        foreach (IGrouping<long, RunEnemyObservationDto> group in detail.EnemyObservations
                     .Where(observation => characterByRun.ContainsKey(observation.RunId))
                     .GroupBy(observation => characterByRun[observation.RunId])
                     .OrderByDescending(group => group.Sum(observation => observation.Count)))
            EnemyCharacterRows.Add(new ActivityEnemyCharacterRowViewModel(
                group.Key, group.Sum(observation => observation.Count), input.NameOf));

        HasEnemyFigures = EnemyCharacterRows.Count > 0;
        EnemyTotalCountText = countedEnemyCount == 1 ? "1 enemy" : $"{countedEnemyCount} enemies";
    }
}
