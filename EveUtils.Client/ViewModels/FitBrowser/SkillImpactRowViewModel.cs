using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One skill in the SKILL IMPACT list: its current level, its value at the next level and at V for every
/// currently-selected stat it moves, and the SP time (prerequisites included) to reach V.</summary>
public sealed class SkillImpactRowViewModel(
    int skillTypeId, string skillName, int currentLevel, IReadOnlyList<SkillImpactStatGain> gains,
    TimeSpan trainingTime, double scorePerHour, Func<Task>? addToPlan = null)
{
    public int SkillTypeId { get; } = skillTypeId;
    public string SkillName { get; } = skillName;
    public int CurrentLevel { get; } = currentLevel;
    public IReadOnlyList<SkillImpactStatGain> Gains { get; } = gains;
    public TimeSpan TrainingTime { get; } = trainingTime;

    /// <summary>The combined winst-per-hour rank (ET-356 point 7) — not shown directly, but what the list sorts by.</summary>
    public double ScorePerHour { get; } = scorePerHour;

    public string TrainingTimeText => TrainingTime <= TimeSpan.Zero
        ? "—"
        : $"{(int)TrainingTime.TotalDays}d {TrainingTime.Hours}h";

    /// <summary>ET-357 D1: adds this skill to V (prerequisites included) to the target plan — enabled only once the
    /// window knows which plan that is (<see cref="SkillImpactViewModel"/>'s own <c>addToPlan</c> delegate).</summary>
    public bool CanAddToPlan { get; } = addToPlan is not null;

    public ICommand AddToPlanCommand { get; } = new AsyncRelayCommand(
        () => addToPlan is null ? Task.CompletedTask : addToPlan(), () => addToPlan is not null);
}
