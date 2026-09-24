using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Sde.Dtos;
using RomanLevel = EveUtils.Client.Skills.RomanLevel;
using SkillPointMath = EveUtils.Shared.Modules.Skills.SkillPointMath;
using SkillQueueEntry = EveUtils.Shared.Modules.Skills.Entities.CharacterSkillQueueEntry;
using SkillQueueStanding = EveUtils.Client.ViewModels.Home.SkillQueueStanding;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>
/// CATALOGUE tab (ET-16 AC): every published skill group (category 16) with how many of its skills are injected,
/// and — once a group is picked — its skills with level pips and either the trained mark, the queued target level,
/// or the plain time to the next level. Read-only.
/// </summary>
public sealed partial class SkillsCatalogueViewModel : ObservableObject
{
    private readonly SkillsCharacterSnapshot _snapshot;
    private readonly Dictionary<int, SdeGroup> _groupsById = new();
    private readonly Dictionary<int, SdeSkill> _skillsById = new();

    public ObservableCollection<SkillGroupTileViewModel> Groups { get; } = [];
    public ObservableCollection<SkillRowViewModel> Skills { get; } = [];

    [ObservableProperty] private SkillGroupTileViewModel? _selectedGroup;
    [ObservableProperty] private SkillRowViewModel? _selectedSkillRow;
    [ObservableProperty] private SkillDetailViewModel? _selectedDetail;
    [ObservableProperty] private int _totalSkillCount;
    [ObservableProperty] private int _injectedSkillCount;
    [ObservableProperty] private int _masteredSkillCount;

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
                continue; // nothing to click into

            _groupsById[group.GroupId] = group;
            foreach (var skill in skills)
                _skillsById[skill.TypeId] = skill;

            int injectedInGroup = skills.Count(s => snapshot.LevelOf(s.TypeId) > 0);
            totalSkills += skills.Count;
            injectedSkills += injectedInGroup;
            masteredSkills += skills.Count(s => snapshot.LevelOf(s.TypeId) == 5);
            Groups.Add(new SkillGroupTileViewModel(group.GroupId, group.Name, injectedInGroup, skills.Count));
        }
        TotalSkillCount = totalSkills;
        InjectedSkillCount = injectedSkills;
        MasteredSkillCount = masteredSkills;

        SelectedGroup = Groups.FirstOrDefault();
    }

    partial void OnSelectedGroupChanged(SkillGroupTileViewModel? value)
    {
        foreach (var g in Groups)
            g.IsSelected = g == value;

        Skills.Clear();
        SelectedSkillRow = null;
        if (value is null)
            return;

        foreach (var skill in _snapshot.Sde.GetSkillsInGroup(value.GroupId).OrderBy(s => s.Name))
            Skills.Add(new SkillRowViewModel(skill.TypeId, skill.Name, _snapshot.LevelOf(skill.TypeId), _StatusText(skill)));
    }

    /// <summary>Builds the detail pane for the picked skill (shared shape with TRAINING QUEUE).</summary>
    partial void OnSelectedSkillRowChanged(SkillRowViewModel? value)
    {
        foreach (var s in Skills)
            s.IsSelected = s == value;

        if (value is null || !_skillsById.TryGetValue(value.SkillTypeId, out var skill))
        {
            SelectedDetail = null;
            return;
        }

        var groupName = SelectedGroup?.Name ?? "";
        var description = _snapshot.Sde.GetType(skill.TypeId)?.Description;
        var queueForSkill = _snapshot.Queue.Where(e => e.SkillTypeId == skill.TypeId).ToList();
        var rate = _snapshot.SpPerMinute(skill.PrimaryAttributeId, skill.SecondaryAttributeId);
        SelectedDetail = new SkillDetailViewModel(skill, groupName, description, _snapshot.LevelOf(skill.TypeId), rate,
            queueForSkill, _snapshot.Now);
    }

    private string _StatusText(SdeSkill skill)
    {
        int level = _snapshot.LevelOf(skill.TypeId);
        if (level >= 5)
            return "✓";

        // A queue entry beyond the trained level, if any — the earliest one not yet trained. Already-past entries
        // never reach here: they are already reflected in Levels (EsiSkillImporter merges a finished queue row into
        // the trained level before this snapshot is built).
        SkillQueueEntry? queued = _snapshot.Queue
            .Where(e => e.SkillTypeId == skill.TypeId && e.FinishedLevel > level && e.FinishDate is not null)
            .OrderBy(e => e.FinishedLevel)
            .FirstOrDefault();
        if (queued is not null)
            return $"QUEUE → {RomanLevel.Text(queued.FinishedLevel)} {SkillQueueStanding.Until(_TimeLeft(queued))}";

        double rate = _snapshot.SpPerMinute(skill.PrimaryAttributeId, skill.SecondaryAttributeId);
        if (rate <= 0)
            return "—";

        double levelSp = SkillPointMath.SkillPointsForLevel(skill.Rank, level + 1)
                        - SkillPointMath.SkillPointsForLevel(skill.Rank, level);
        return SkillQueueStanding.Until(TimeSpan.FromMinutes(levelSp / rate));
    }

    private TimeSpan _TimeLeft(SkillQueueEntry entry) =>
        entry.FinishDate is { } finish && finish > _snapshot.Now ? finish - _snapshot.Now : TimeSpan.Zero;
}
