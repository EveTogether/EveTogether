using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Skills;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One skill in the SKILL IMPACT list (mockup v5 pane): the level step it would train next, what that level
/// does to every chosen stat it moves and how long it takes, and the same to V — prerequisites included in both times.</summary>
public sealed class SkillImpactRowViewModel(
    int skillTypeId, string skillName, int currentLevel, IReadOnlyList<SkillImpactStatGain> gains,
    TimeSpan nextLevelTime, TimeSpan trainingTime, double scorePerHour, Func<Task>? addToPlan = null)
{
    public int SkillTypeId { get; } = skillTypeId;
    public string SkillName { get; } = skillName;
    public int CurrentLevel { get; } = currentLevel;
    public IReadOnlyList<SkillImpactStatGain> Gains { get; } = gains;
    public TimeSpan NextLevelTime { get; } = nextLevelTime;
    public TimeSpan TrainingTime { get; } = trainingTime;

    /// <summary>The combined gain per hour of the next level — what the list sorts by.</summary>
    public double ScorePerHour { get; } = scorePerHour;

    /// <summary>The first row is drawn selected, the way the mockup marks the skill to train first.</summary>
    public bool IsFirst { get; set; }

    public string LevelStepText => CurrentLevel == 0
        ? $"0→{RomanLevel.Text(1)}"
        : $"{RomanLevel.Text(CurrentLevel)}→{RomanLevel.Text(CurrentLevel + 1)}";

    public string NextGainText => string.Join(" · ", Gains.Select(gain => gain.NextText));

    public string NextTimeText => EveDurationFormatter.Format(NextLevelTime);

    public string ToFiveText => $"to V: {string.Join(" · ", Gains.Select(gain => gain.FiveText))} · {EveDurationFormatter.Format(TrainingTime)}";

    /// <summary>Adds this skill to V (prerequisites included) to the target plan — shown only once the window knows
    /// which plan that is (<see cref="SkillImpactViewModel"/>'s own <c>addToPlan</c> delegate).</summary>
    public bool CanAddToPlan { get; } = addToPlan is not null;

    public ICommand AddToPlanCommand { get; } = new AsyncRelayCommand(
        () => addToPlan is null ? Task.CompletedTask : addToPlan(), () => addToPlan is not null);
}
