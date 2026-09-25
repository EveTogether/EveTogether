using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Sde.Dtos;
using SkillPointMath = EveUtils.Shared.Modules.Skills.SkillPointMath;
using SkillQueueEntry = EveUtils.Shared.Modules.Skills.Entities.CharacterSkillQueueEntry;
using SkillQueueStanding = EveUtils.Client.ViewModels.Home.SkillQueueStanding;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>
/// TRAINING QUEUE tab (ET-16 AC3-5): only rows whose FinishDate has not already passed, in queue order, reusing
/// <see cref="SkillQueueStanding"/>'s own wording and thresholds for the head-of-queue summary — the same tile the
/// home dashboard shows (ET-324). A paused queue (ESI omits every date) shows ≈ estimates instead of dates.
/// </summary>
public sealed partial class SkillsQueueViewModel : ObservableObject
{
    /// <summary>ESI caps the training queue at this many entries.</summary>
    public const int QueueCap = 150;

    private readonly SkillsCharacterSnapshot _snapshot;
    private readonly Dictionary<int, SdeSkill> _skillsById = new();
    private readonly Dictionary<int, string> _groupNameBySkill = new();

    public ObservableCollection<SkillQueueRowViewModel> Rows { get; } = [];

    [ObservableProperty] private SkillQueueRowViewModel? _selectedRow;
    [ObservableProperty] private SkillDetailViewModel? _selectedDetail;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _hasQueue;
    [ObservableProperty] private string _trainingNowText = "";
    [ObservableProperty] private string _queueLeftText = "";
    [ObservableProperty] private string _skillCountText = $"0/{QueueCap}";
    [ObservableProperty] private string _distinctSkillsText = "";
    [ObservableProperty] private string _spInQueueText = "—";

    public SkillsQueueViewModel(SkillsCharacterSnapshot snapshot)
    {
        _snapshot = snapshot;

        foreach (var group in snapshot.Sde.GetGroupsByCategory(16))
        {
            foreach (var skill in snapshot.Sde.GetSkillsInGroup(group.GroupId))
            {
                _skillsById[skill.TypeId] = skill;
                _groupNameBySkill[skill.TypeId] = group.Name;
            }
        }

        // AC3: a row whose FinishDate has already passed is never shown — a finished row belongs to Levels
        // (EsiSkillImporter merges it there) and staying in the raw queue store is a stale leftover, not a fact to
        // display. A paused row (FinishDate null) is not "in the past" and stays.
        var future = snapshot.Queue
            .Where(e => e.FinishDate is null || e.FinishDate > snapshot.Now)
            .OrderBy(e => e.QueuePosition)
            .ToList();

        HasQueue = future.Count > 0;
        SkillCountText = $"{future.Count}/{QueueCap}";
        DistinctSkillsText = $"{future.Select(e => e.SkillTypeId).Distinct().Count()} distinct skills";

        var standing = SkillQueueStanding.From(future, id => _skillsById.TryGetValue(id, out var s) ? s.Name : $"type {id}");
        IsPaused = standing?.IsPaused ?? false;
        TrainingNowText = standing?.SkillText ?? "—";
        QueueLeftText = standing?.DetailText(snapshot.Now) ?? "queue paused · 0 waiting";

        DateTimeOffset previousEnd = snapshot.Now;
        TimeSpan pausedCumulative = TimeSpan.Zero;
        long spTotal = 0;
        int position = 0;
        foreach (var entry in future)
        {
            position++;
            if (!_skillsById.TryGetValue(entry.SkillTypeId, out var skill))
            {
                continue;
            }

            long levelSp = (long)(SkillPointMath.SkillPointsForLevel(skill.Rank, entry.FinishedLevel)
                                 - SkillPointMath.SkillPointsForLevel(skill.Rank, entry.FinishedLevel - 1));
            spTotal += levelSp;
            string groupName = _groupNameBySkill.GetValueOrDefault(entry.SkillTypeId, "");

            string thisLevelText, endsText, fromNowText;
            if (entry.FinishDate is { } finish)
            {
                var thisLevel = _NotNegative(finish - previousEnd);
                var fromNow = _NotNegative(finish - snapshot.Now);
                thisLevelText = SkillQueueStanding.Until(thisLevel);
                endsText = finish.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
                fromNowText = SkillQueueStanding.Until(fromNow);
                previousEnd = finish;
            }
            else
            {
                double rate = snapshot.SpPerMinute(skill.PrimaryAttributeId, skill.SecondaryAttributeId);
                var estimate = rate > 0 ? TimeSpan.FromMinutes(levelSp / rate) : TimeSpan.Zero;
                pausedCumulative += estimate;
                thisLevelText = "≈" + SkillQueueStanding.Until(estimate);
                endsText = "—";
                fromNowText = "≈" + SkillQueueStanding.Until(pausedCumulative);
            }

            Rows.Add(new SkillQueueRowViewModel(position, skill.TypeId, skill.Name, snapshot.LevelOf(skill.TypeId),
                entry.FinishedLevel, groupName, position == 1, thisLevelText, endsText, fromNowText));
        }

        SpInQueueText = spTotal > 0 ? $"{(spTotal / 1_000_000.0).ToString("0.00", CultureInfo.InvariantCulture)}M" : "—";
    }

    /// <summary>The same detail pane shape as CATALOGUE, for whichever row is picked.</summary>
    partial void OnSelectedRowChanged(SkillQueueRowViewModel? value)
    {
        foreach (var r in Rows)
        {
            r.IsSelected = r == value;
        }

        if (value is null || !_skillsById.TryGetValue(value.SkillTypeId, out var skill))
        {
            SelectedDetail = null;
            return;
        }

        var groupName = _groupNameBySkill.GetValueOrDefault(value.SkillTypeId, "");
        var description = _snapshot.Sde.GetType(skill.TypeId)?.Description;
        var queueForSkill = _snapshot.Queue.Where(e => e.SkillTypeId == skill.TypeId).ToList();
        var rate = _snapshot.SpPerMinute(skill.PrimaryAttributeId, skill.SecondaryAttributeId);
        SelectedDetail = new SkillDetailViewModel(skill, groupName, description, _snapshot.LevelOf(skill.TypeId), rate,
            queueForSkill, _snapshot.Now);
    }

    private static TimeSpan _NotNegative(TimeSpan span) => span < TimeSpan.Zero ? TimeSpan.Zero : span;
}
