using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Skills;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Entities;
using SkillQueueStanding = EveUtils.Client.ViewModels.Home.SkillQueueStanding;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>One of the five attribute boxes under TRAINING RATE; the skill's primary and secondary are highlighted.</summary>
public sealed record AttributeBoxViewModel(string Label, string Value, bool IsUsed);

/// <summary>
/// The 400 px detail pane for one skill (ET-16, laid out as mockup v5): group, rank and attributes; the level pips;
/// the SDE description; LEVELS with total SP, the time per level at today's attributes and its queue state; TRAINING
/// RATE with the five attributes; and IN THE QUEUE with a jump to the queue. Shown from CATALOGUE and TRAINING QUEUE.
/// </summary>
public sealed partial class SkillDetailViewModel
{
    private static readonly (int AttributeId, string Label)[] _Boxes =
    [
        (DogmaAttributeIds.Charisma, "CHAR"), (DogmaAttributeIds.Intelligence, "INTE"), (DogmaAttributeIds.Memory, "MEMO"),
        (DogmaAttributeIds.Perception, "PERC"), (DogmaAttributeIds.Willpower, "WILL"),
    ];

    private readonly Action<int>? _showInQueue;

    public int SkillTypeId { get; }
    public string Name { get; }
    public string GroupName { get; }
    public int Rank { get; }
    public string RankText => $"Rank ×{Rank}";
    public string AttributesText { get; }
    public int CurrentLevel { get; }
    public int QueuedLevel { get; }
    public int TrainingLevel { get; }
    public string LevelText { get; }
    public string PipsText { get; }
    public string? Description { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public double SpPerMinute { get; }
    public bool HasTrainingRate => SpPerMinute > 0;
    public string TrainingRateText => HasTrainingRate
        ? $"{SpPerMinute.ToString("0.0", CultureInfo.InvariantCulture)} SP/min · {(SpPerMinute * 60).ToString("N0", CultureInfo.InvariantCulture)} SP/h (Omega)"
        : "No attributes on file for this character.";
    public IReadOnlyList<SkillLevelRowViewModel> Levels { get; }
    public IReadOnlyList<AttributeBoxViewModel> AttributeBoxes { get; }
    public string ImplantNote { get; }
    public string InQueueText { get; }
    public bool IsInQueue { get; }
    public bool CanShowInQueue => IsInQueue && _showInQueue is not null;

    public SkillDetailViewModel(SdeSkill skill, string groupName, SkillsCharacterSnapshot snapshot, Action<int>? showInQueue = null)
    {
        _showInQueue = showInQueue;
        SkillTypeId = skill.TypeId;
        Name = skill.Name;
        GroupName = groupName;
        Rank = skill.Rank;
        AttributesText = $"{SkillAttributeLookup.Name(skill.PrimaryAttributeId)} / {SkillAttributeLookup.Name(skill.SecondaryAttributeId)}";
        CurrentLevel = snapshot.LevelOf(skill.TypeId);
        Description = snapshot.Sde.GetType(skill.TypeId)?.Description;
        SpPerMinute = snapshot.SpPerMinute(skill.PrimaryAttributeId, skill.SecondaryAttributeId);
        var now = snapshot.Now;

        var queueForSkill = snapshot.Queue
            .Where(e => e.SkillTypeId == skill.TypeId && (e.FinishDate is null || e.FinishDate > now))
            .OrderBy(e => e.QueuePosition)
            .ToList();
        var levels = new List<SkillLevelRowViewModel>(5);
        int trainingLevel = 0;
        for (int level = 1; level <= 5; level++)
        {
            long totalSp = (long)SkillPointMath.SkillPointsForLevel(skill.Rank, level);
            long levelSp = totalSp - (long)SkillPointMath.SkillPointsForLevel(skill.Rank, level - 1);
            bool trained = level <= CurrentLevel;
            var queueEntry = queueForSkill.FirstOrDefault(e => e.FinishedLevel == level);
            string time = SpPerMinute > 0 ? SkillQueueStanding.Until(TimeSpan.FromMinutes(levelSp / SpPerMinute)) : "—";

            string status;
            bool training = false;
            if (trained)
            {
                status = "✓ trained";
            }
            else if (queueEntry is not null)
            {
                training = queueEntry.StartDate is { } start && start <= now && queueEntry.FinishDate is not null;
                if (training)
                {
                    trainingLevel = level;
                }

                status = queueEntry.FinishDate is { } finish
                    ? $"{(training ? "training" : "queued")} · {SkillsQueueViewModel.When(finish, now)}"
                    : "queued · paused";
            }
            else
            {
                status = "—";
            }

            levels.Add(new SkillLevelRowViewModel(level, RomanLevel.Text(level), totalSp, trained, time, status, training,
                queueEntry is not null));
        }

        TrainingLevel = trainingLevel;
        QueuedLevel = queueForSkill.Select(e => e.FinishedLevel).DefaultIfEmpty(0).Max();
        PipsText = SkillLevelPips.Text(CurrentLevel, trainingLevel == 0 ? null : trainingLevel);
        LevelText = $"Level {CurrentLevel} · {((long)SkillPointMath.SkillPointsForLevel(skill.Rank, CurrentLevel)).ToString("N0", CultureInfo.InvariantCulture)} SP";
        Levels = levels;

        AttributeBoxes = _Boxes.Select(box => new AttributeBoxViewModel(box.Label,
            snapshot.Attributes is { } attributes ? SkillAttributeLookup.Value(attributes, box.AttributeId).ToString("0", CultureInfo.InvariantCulture) : "—",
            box.AttributeId == skill.PrimaryAttributeId || box.AttributeId == skill.SecondaryAttributeId)).ToList();
        ImplantNote = snapshot.ImplantNote;

        IsInQueue = queueForSkill.Count > 0;
        InQueueText = IsInQueue
            ? $"{(queueForSkill.Count == 1 ? "Position" : "Positions")} {string.Join(", ", queueForSkill.Select(e => _Position(snapshot, e)))} · level " +
              (queueForSkill.Count == 1 ? RomanLevel.Text(QueuedLevel) : $"{RomanLevel.Text(queueForSkill.Min(e => e.FinishedLevel))}–{RomanLevel.Text(QueuedLevel)}") +
              (queueForSkill.Max(e => e.FinishDate) is { } done ? $" · done {SkillsQueueViewModel.When(done, now)}" : " · paused")
            : "Not in the queue.";
    }

    [RelayCommand]
    private void ShowInQueue() => _showInQueue?.Invoke(SkillTypeId);

    // The 1-based place in the visible queue, the same numbering as the TRAINING QUEUE table.
    private static int _Position(SkillsCharacterSnapshot snapshot, CharacterSkillQueueEntry entry) =>
        snapshot.Queue.Where(e => e.FinishDate is null || e.FinishDate > snapshot.Now)
            .OrderBy(e => e.QueuePosition).ToList().IndexOf(entry) + 1;
}
