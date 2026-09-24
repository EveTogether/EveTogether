using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveUtils.Client.Skills;
using EveUtils.Client.ViewModels.Home;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Entities;

namespace EveUtils.Client.ViewModels.Skills;

/// <summary>
/// The 400 px detail pane for one skill (ET-16), shown from either CATALOGUE or TRAINING QUEUE — the same read for
/// a skill regardless of which list it was opened from. Read-only: level pips, the SDE description, the I-V
/// breakdown (total SP and, per level, trained / training / queued / the plain time-to-train estimate) and the
/// training rate for the character currently selected.
/// </summary>
public sealed class SkillDetailViewModel
{
    public int SkillTypeId { get; }
    public string Name { get; }
    public string GroupName { get; }
    public int Rank { get; }
    public string AttributesText { get; }
    public int CurrentLevel { get; }
    public string PipsText { get; }
    public string? Description { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public double SpPerMinute { get; }
    public bool HasTrainingRate => SpPerMinute > 0;
    public string TrainingRateText => HasTrainingRate
        ? $"{SpPerMinute.ToString("0.0", CultureInfo.InvariantCulture)} SP/min · {(SpPerMinute * 60).ToString("N0", CultureInfo.InvariantCulture)} SP/h (Omega)"
        : "No attributes on file for this character.";
    public IReadOnlyList<SkillLevelRowViewModel> Levels { get; }

    public SkillDetailViewModel(SdeSkill skill, string groupName, string? description, int currentLevel,
        double spPerMinute, IReadOnlyList<CharacterSkillQueueEntry> queueForSkill, DateTimeOffset now)
    {
        SkillTypeId = skill.TypeId;
        Name = skill.Name;
        GroupName = groupName;
        Rank = skill.Rank;
        AttributesText = $"{SkillAttributeLookup.Name(skill.PrimaryAttributeId)} / {SkillAttributeLookup.Name(skill.SecondaryAttributeId)}";
        CurrentLevel = currentLevel;
        Description = description;
        SpPerMinute = spPerMinute;

        var levels = new List<SkillLevelRowViewModel>(5);
        int? trainingLevel = null;
        for (int level = 1; level <= 5; level++)
        {
            long totalSp = (long)SkillPointMath.SkillPointsForLevel(skill.Rank, level);
            long levelSp = totalSp - (long)SkillPointMath.SkillPointsForLevel(skill.Rank, level - 1);
            bool trained = level <= currentLevel;
            var queueEntry = queueForSkill.FirstOrDefault(e => e.FinishedLevel == level);

            string status;
            if (trained)
            {
                status = "✓";
            }
            else if (queueEntry is not null)
            {
                bool training = _IsCurrentlyTraining(queueEntry, now);
                if (training)
                {
                    trainingLevel = level;
                }
                status = queueEntry.FinishDate is { } finish
                    ? $"{(training ? "training" : "queued")} · {finish.ToLocalTime():ddd d MMM HH:mm}"
                    : "queue paused";
            }
            else
            {
                status = spPerMinute > 0 ? SkillQueueStanding.Until(TimeSpan.FromMinutes(levelSp / spPerMinute)) : "—";
            }

            levels.Add(new SkillLevelRowViewModel(level, RomanLevel.Text(level), totalSp, trained, status));
        }
        PipsText = SkillLevelPips.Text(currentLevel, trainingLevel);
        Levels = levels;
    }

    private static bool _IsCurrentlyTraining(CharacterSkillQueueEntry entry, DateTimeOffset now) =>
        entry.StartDate is { } start && start <= now;
}
