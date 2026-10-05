using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Sde.Dtos;
using RomanLevel = EveUtils.Client.Skills.RomanLevel;
using SkillPointMath = EveUtils.Shared.Modules.Skills.SkillPointMath;
using SkillQueueStanding = EveUtils.Client.ViewModels.Home.SkillQueueStanding;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>
/// CATALOGUE tab (ET-16, laid out as mockup v5 screen a): a filter and a search over all skills, every published skill
/// group (category 16) as a tile with its injected count and the share of levels trained, and the picked group's
/// skills in two columns with their pips and state. Read-only.
/// </summary>
public sealed partial class SkillsCatalogueViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> Filters = ["All skills", "Injected", "Not injected", "In the queue", "Not at V"];

    private readonly SkillsCharacterSnapshot _snapshot;
    private readonly Dictionary<int, SdeSkill> _skillsById = new();
    private readonly Dictionary<int, string> _groupNameBySkill = new();

    public ObservableCollection<SkillGroupTileViewModel> Groups { get; } = [];
    public ObservableCollection<SkillRowViewModel> Skills { get; } = [];
    public IReadOnlyList<string> FilterOptions => Filters;

    [ObservableProperty] private SkillGroupTileViewModel? _selectedGroup;
    [ObservableProperty] private SkillRowViewModel? _selectedSkillRow;
    [ObservableProperty] private SkillDetailViewModel? _selectedDetail;
    [ObservableProperty] private int _totalSkillCount;
    [ObservableProperty] private int _injectedSkillCount;
    [ObservableProperty] private int _masteredSkillCount;
    [ObservableProperty] private string _selectedFilter = Filters[0];
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _listTitle = "";
    [ObservableProperty] private string _listSummary = "";

    /// <summary>Set by the window: SHOW IN QUEUE in the pane jumps to TRAINING QUEUE on this skill.</summary>
    public Action<int>? ShowInQueue { get; set; }

    public string SummaryText =>
        $"{InjectedSkillCount.ToString(CultureInfo.InvariantCulture)} of {TotalSkillCount.ToString(CultureInfo.InvariantCulture)} skills injected · " +
        $"{MasteredSkillCount.ToString(CultureInfo.InvariantCulture)} at V";

    public SkillsCatalogueViewModel(SkillsCharacterSnapshot snapshot)
    {
        _snapshot = snapshot;

        int totalSkills = 0, injectedSkills = 0, masteredSkills = 0;
        foreach (var group in snapshot.Sde.GetGroupsByCategory(16).OrderBy(g => g.Name))
        {
            var skills = snapshot.Sde.GetSkillsInGroup(group.GroupId);
            if (skills.Count == 0)
            {
                continue; // nothing to click into
            }

            foreach (var skill in skills)
            {
                _skillsById[skill.TypeId] = skill;
                _groupNameBySkill[skill.TypeId] = group.Name;
            }

            int injectedInGroup = skills.Count(s => snapshot.LevelOf(s.TypeId) > 0);
            double trainedShare = skills.Sum(s => snapshot.LevelOf(s.TypeId)) / (5.0 * skills.Count);
            totalSkills += skills.Count;
            injectedSkills += injectedInGroup;
            masteredSkills += skills.Count(s => snapshot.LevelOf(s.TypeId) == 5);
            Groups.Add(new SkillGroupTileViewModel(group.GroupId, group.Name, injectedInGroup, skills.Count, trainedShare));
        }
        TotalSkillCount = totalSkills;
        InjectedSkillCount = injectedSkills;
        MasteredSkillCount = masteredSkills;

        SelectedGroup = Groups.FirstOrDefault(g => !g.IsEmpty) ?? Groups.FirstOrDefault();
    }

    partial void OnSelectedGroupChanged(SkillGroupTileViewModel? value)
    {
        foreach (var g in Groups)
        {
            g.IsSelected = g == value;
        }

        _RebuildList();
    }

    partial void OnSelectedFilterChanged(string value) => _RebuildList();

    partial void OnSearchTextChanged(string value) => _RebuildList();

    // The list under the block: the picked group, or with a search text every matching skill; both through the filter.
    private void _RebuildList()
    {
        Skills.Clear();
        SelectedSkillRow = null;
        bool searching = !string.IsNullOrWhiteSpace(SearchText);
        IEnumerable<SdeSkill> source = searching
            ? _skillsById.Values.Where(s => s.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase))
            : SelectedGroup is { } group ? _snapshot.Sde.GetSkillsInGroup(group.GroupId) : [];
        var skills = source.Where(_PassesFilter).OrderBy(s => s.Name).ToList();
        foreach (var skill in skills)
        {
            Skills.Add(_Row(skill));
        }

        int injected = skills.Count(s => _snapshot.LevelOf(s.TypeId) > 0);
        int atFive = skills.Count(s => _snapshot.LevelOf(s.TypeId) == 5);
        int inQueue = skills.Count(s => _QueuedLevel(s.TypeId) > 0);
        ListTitle = searching ? $"SEARCH · \"{SearchText.Trim()}\"" : (SelectedGroup?.Name ?? "").ToUpperInvariant();
        ListSummary = $"{injected} of {skills.Count} injected · {atFive} at V · {inQueue} in queue";
    }

    private bool _PassesFilter(SdeSkill skill)
    {
        int level = _snapshot.LevelOf(skill.TypeId);
        return SelectedFilter switch
        {
            "Injected" => level > 0,
            "Not injected" => level == 0,
            "In the queue" => _QueuedLevel(skill.TypeId) > 0,
            "Not at V" => level < 5,
            _ => true,
        };
    }

    /// <summary>Builds the detail pane for the picked skill (shared shape with TRAINING QUEUE).</summary>
    partial void OnSelectedSkillRowChanged(SkillRowViewModel? value)
    {
        foreach (var s in Skills)
        {
            s.IsSelected = s == value;
        }

        if (value is null || !_skillsById.TryGetValue(value.SkillTypeId, out var skill))
        {
            SelectedDetail = null;
            return;
        }

        SelectedDetail = new SkillDetailViewModel(skill, _groupNameBySkill.GetValueOrDefault(skill.TypeId, ""), _snapshot,
            id => ShowInQueue?.Invoke(id));
    }

    private SkillRowViewModel _Row(SdeSkill skill)
    {
        int level = _snapshot.LevelOf(skill.TypeId);
        int queuedLevel = _QueuedLevel(skill.TypeId);
        int trainingLevel = _snapshot.TrainingHead is { } head && head.SkillTypeId == skill.TypeId ? head.Level : 0;
        string description = _snapshot.Sde.GetType(skill.TypeId)?.Description ?? "";
        bool injected = level > 0 || queuedLevel > 0;

        if (level >= 5)
        {
            return new SkillRowViewModel(skill.TypeId, skill.Name, description, level, 0, 0, true, "", "", "✓");
        }

        if (queuedLevel > level)
        {
            var lastQueued = _snapshot.Queue
                .Where(e => e.SkillTypeId == skill.TypeId && e.FinishedLevel == queuedLevel)
                .Select(e => e.FinishDate).FirstOrDefault();
            string time = lastQueued is { } finish && finish > _snapshot.Now ? SkillQueueStanding.Until(finish - _snapshot.Now) : "paused";
            return new SkillRowViewModel(skill.TypeId, skill.Name, description, level, queuedLevel, trainingLevel, true,
                "", $"QUEUE → {RomanLevel.Text(queuedLevel)}", time);
        }

        double rate = _snapshot.SpPerMinute(skill.PrimaryAttributeId, skill.SecondaryAttributeId);
        double levelSp = SkillPointMath.SkillPointsForLevel(skill.Rank, level + 1) - SkillPointMath.SkillPointsForLevel(skill.Rank, level);
        string next = rate > 0 ? SkillQueueStanding.Until(TimeSpan.FromMinutes(levelSp / rate)) : "—";
        string status = injected ? $"{RomanLevel.Text(level + 1)} in {next}" : $"not injected · {next}";
        return new SkillRowViewModel(skill.TypeId, skill.Name, description, level, 0, trainingLevel, injected, status, "", "");
    }

    // The highest level of this skill still waiting in the queue (0 when none); finished rows are already in Levels.
    private int _QueuedLevel(int skillTypeId) => _snapshot.Queue
        .Where(e => e.SkillTypeId == skillTypeId && (e.FinishDate is null || e.FinishDate > _snapshot.Now))
        .Select(e => e.FinishedLevel).DefaultIfEmpty(0).Max();
}
