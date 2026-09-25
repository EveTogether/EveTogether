using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One stat's share on a target card — the gekozen stat's label and its 0-100% position between can fly and
/// max (ET-357's <c>StatShare</c>, can-fly-to-max range).</summary>
public sealed record SkillTargetStatShareViewModel(string Label, double Share)
{
    public string PercentText => $"{Share * 100:0}%";
}

/// <summary>
/// One of the three SKILL IMPACT cards (ET-357): can fly, optimal ±III or max. Training time/date/SP/levels, the
/// chosen stats' share of the can-fly-to-max range, the fit-check outcome, and ADD TO PLAN — wired only once a target
/// plan is known (<see cref="SkillImpactViewModel"/>'s <c>addToPlan</c> delegate), disabled otherwise.
/// </summary>
public sealed class SkillTargetCardViewModel
{
    public SkillTargetCardViewModel(SkillTargetGoal goal, IReadOnlyDictionary<SkillImpactStat, string> labels,
        DateTimeOffset now, Func<Task>? addToPlan)
    {
        Title = goal.Kind switch
        {
            SkillTargetGoalKind.CanFly => "CAN FLY",
            SkillTargetGoalKind.Optimal => "OPTIMAL ±III",
            SkillTargetGoalKind.Max => "MAX",
            _ => goal.Kind.ToString(),
        };
        LevelsText = $"{goal.Levels.Count} level{(goal.Levels.Count == 1 ? "" : "s")}";
        SpText = $"{goal.SkillPoints.ToString("N0", CultureInfo.InvariantCulture)} SP";
        TimeText = goal.TrainingTime <= TimeSpan.Zero
            ? "already trained"
            : $"{(int)goal.TrainingTime.TotalDays}d {goal.TrainingTime.Hours}h";
        DateText = goal.TrainingTime <= TimeSpan.Zero ? "" : $"done {now.Add(goal.TrainingTime):ddd d MMM HH:mm}";
        ScorePercentText = $"{goal.Score * 100:0}%";
        StatShares = goal.StatShares
            .Select(pair => new SkillTargetStatShareViewModel(labels.GetValueOrDefault(pair.Key, pair.Key.ToString()), pair.Value))
            .ToList();

        Fits = goal.Fits;
        FitCheckText = goal.Fits ? "Fits" : _ShortfallText(goal.Shortfalls);

        bool canAdd = addToPlan is not null && goal.Levels.Count > 0;
        CanAddToPlan = canAdd;
        AddToPlanCommand = new AsyncRelayCommand(() => addToPlan is null ? Task.CompletedTask : addToPlan(), () => canAdd);
    }

    public string Title { get; }
    public string LevelsText { get; }
    public string SpText { get; }
    public string TimeText { get; }
    public string DateText { get; }
    public string ScorePercentText { get; }
    public IReadOnlyList<SkillTargetStatShareViewModel> StatShares { get; }
    public bool Fits { get; }
    public string FitCheckText { get; }
    public bool CanAddToPlan { get; }
    public ICommand AddToPlanCommand { get; }

    private static string _ShortfallText(IReadOnlyList<SkillTargetResourceShortfall> shortfalls)
    {
        string resources = string.Join(", ", shortfalls.Select(shortfall =>
            $"{(shortfall.Resource == SkillImpactStat.FreeCpu ? "CPU" : "PG")} short {shortfall.Shortfall.ToString("0.#", CultureInfo.InvariantCulture)}"));
        return $"This fit does not fit at any skill level ({resources})";
    }
}
