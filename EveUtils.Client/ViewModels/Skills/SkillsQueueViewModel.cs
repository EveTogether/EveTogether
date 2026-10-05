using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>The REMAP line's text ("A remap would save X; a +4 implant set Y") — set from the OPTIMISE tab's
    /// own view-model once both are built (ET-354). Empty until then, which hides the line.</summary>
    [ObservableProperty] private string _remapLineText = "";

    // Mockup v5's stat row: the training skill with its progress and end, the queue's length and end, the entry count
    // and the SP still to train.
    [ObservableProperty] private double _trainingProgress;
    [ObservableProperty] private string _trainingEndsText = "";
    [ObservableProperty] private string _queueLeftBigText = "—";
    [ObservableProperty] private string _queueUntilText = "";
    [ObservableProperty] private string _queuedCountText = "0";
    [ObservableProperty] private string _spInQueueNumberText = "—";
    [ObservableProperty] private string _timelineMidText = "";
    [ObservableProperty] private string _timelineEndText = "";
    [ObservableProperty] private IReadOnlyList<double> _timelineBoundaries = [];

    /// <summary>Jumps the SKILLS window to the OPTIMISE tab — wired by <see cref="SkillsWindowViewModel"/>, which
    /// owns <c>SelectedTabIndex</c>.</summary>
    public Action? GoToOptimise { get; set; }

    [RelayCommand]
    private void OpenOptimise() => GoToOptimise?.Invoke();

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
        QueuedCountText = future.Count.ToString(CultureInfo.InvariantCulture);
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
                endsText = When(finish, snapshot.Now);
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
        SpInQueueNumberText = spTotal > 0 ? (spTotal / 1_000_000.0).ToString("0.00", CultureInfo.InvariantCulture) : "—";
        _ApplyStatsAndTimeline(future);
        SelectedRow = Rows.FirstOrDefault(); // the pane shows the training skill until the pilot picks another row
    }

    private void _ApplyStatsAndTimeline(IReadOnlyList<SkillQueueEntry> future)
    {
        var now = _snapshot.Now;
        var head = future.FirstOrDefault();
        if (head is { FinishDate: { } headFinish })
        {
            var start = head.StartDate ?? now;
            double span = (headFinish - start).TotalMinutes;
            TrainingProgress = span > 0 ? Math.Clamp((now - start).TotalMinutes / span, 0, 1) : 0;
            TrainingEndsText = $"ends {When(headFinish, now)} · in {SkillQueueStanding.Until(_NotNegative(headFinish - now))}";
        }
        else
        {
            TrainingProgress = 0;
            TrainingEndsText = head is null ? "" : "paused · nothing trains until the queue resumes";
        }

        var ends = future.Select(e => e.FinishDate).OfType<DateTimeOffset>().ToList();
        if (ends.Count > 0 && !IsPaused)
        {
            var end = ends.Max();
            QueueLeftBigText = SkillQueueStanding.Until(_NotNegative(end - now));
            QueueUntilText = $"until {When(end, now)}";
            double total = (end - now).TotalMinutes;
            TimelineBoundaries = total > 0 ? ends.Take(ends.Count - 1).Select(e => Math.Clamp((e - now).TotalMinutes / total, 0, 1)).ToList() : [];
            TimelineMidText = When(now + (end - now) / 2, now);
            TimelineEndText = When(end, now);
        }
        else
        {
            QueueLeftBigText = Rows.LastOrDefault()?.FromNowText ?? "—";
            QueueUntilText = head is null ? "nothing queued" : "queue paused · no end date";
            TimelineBoundaries = [];
            TimelineMidText = "";
            TimelineEndText = "";
        }
    }

    /// <summary>"today 11:47", "tomorrow 09:05", "Sat 26 Sep 01:22", or with the year when it is not this year.</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        string time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today)
        {
            return $"today {time}";
        }

        if (local.Date == today.AddDays(1))
        {
            return $"tomorrow {time}";
        }

        return local.Year == today.Year
            ? local.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Selects the first visible row for a skill — CATALOGUE's SHOW IN QUEUE lands here.</summary>
    public void SelectSkill(int skillTypeId) => SelectedRow = Rows.FirstOrDefault(r => r.SkillTypeId == skillTypeId) ?? SelectedRow;

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
        SelectedDetail = new SkillDetailViewModel(skill, groupName, _snapshot);
    }

    private static TimeSpan _NotNegative(TimeSpan span) => span < TimeSpan.Zero ? TimeSpan.Zero : span;
}
