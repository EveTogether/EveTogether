using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Skills;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One chosen stat on a target card: its value at the card's levels and its 0-100% position between can fly
/// and max (ET-357's <c>StatShare</c>).</summary>
public sealed record SkillTargetStatShareViewModel(string Label, string ValueText, double Share)
{
    public string PercentText => $"{Share * 100:0}%";
    public bool IsFull => Share >= 0.999;
}

/// <summary>"✓ CPU fits · 23.4 tf free" / "✗ PG short 480 MW".</summary>
public sealed record SkillTargetFitLineViewModel(string Text, bool Fits);

/// <summary>
/// One of the three SKILL IMPACT cards (ET-357, mockup v5 "f · from a fit"): can fly + fits, optimal ±III or max ·
/// V, each with the rule that built it, the training time and finish date, the chosen stats with their value and
/// share of what skills can add, the CPU/PG fit check, and ADD TO PLAN when a target plan is known.
/// </summary>
public sealed class SkillTargetCardViewModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public SkillTargetCardViewModel(SkillTargetGoal goal, IReadOnlyList<SkillImpactStatChipViewModel> chosen,
        SkillTargetsResult result, ISdeNameResolver names, DateTimeOffset now, Func<Task>? addToPlan)
    {
        string statList = SkillImpactStats.RuleList(chosen.Select(chip => chip.RuleName).ToList());
        (Title, RuleText) = goal.Kind switch
        {
            SkillTargetGoalKind.CanFly => ("CAN FLY + FITS", result.FittingSkills.Count == 0
                ? "the fit's requirements"
                : "the fit's requirements, plus the fitting skills it needs to fit: " + string.Join(", ",
                    result.FittingSkills.Select(level => $"{names.TypeName(level.SkillTypeId)} {RomanLevel.Text(level.Level)}"))),
            SkillTargetGoalKind.Optimal => ("OPTIMAL · ±III", $"the skills that move {statList} at III or more; higher where the fit asks for it"),
            _ => ("MAX · V", $"the skills that move {statList} at V"),
        };
        IsRecommended = goal.Kind == SkillTargetGoalKind.Optimal;

        bool trained = goal.TrainingTime <= TimeSpan.Zero;
        TimeText = trained ? "already trained" : EveDurationFormatter.Format(goal.TrainingTime);
        var done = now.Add(goal.TrainingTime).ToLocalTime();
        DonePrefix = trained ? "" : "done ";
        DateText = trained ? "nothing to train" : done.ToString(done.Year == now.ToLocalTime().Year ? "ddd d MMM HH:mm" : "ddd d MMM yyyy HH:mm", Inv);
        DetailText = $" · {goal.Levels.Count} level{(goal.Levels.Count == 1 ? "" : "s")} · {_Sp(goal.SkillPoints)} SP";

        var afterCanFly = goal.TrainingTime - result.CanFly.TrainingTime;
        AfterCanFlyText = goal.Kind == SkillTargetGoalKind.CanFly ? null
            : afterCanFly > TimeSpan.Zero ? $"+{EveDurationFormatter.Format(afterCanFly)} after can fly" : "nothing after can fly";

        StatShares = chosen
            .Where(chip => goal.Values.ContainsKey(chip.Stat))
            .Select(chip => new SkillTargetStatShareViewModel(chip.Label, SkillImpactStats.Value(chip.Stat, goal.Values[chip.Stat]),
                goal.StatShares.GetValueOrDefault(chip.Stat)))
            .ToList();

        FitLines =
        [
            _FitLine("CPU", SkillImpactStat.FreeCpu, goal.Values[SkillImpactStat.FreeCpu]),
            _FitLine("PG", SkillImpactStat.FreePg, goal.Values[SkillImpactStat.FreePg]),
        ];

        bool canAdd = addToPlan is not null && goal.Levels.Count > 0;
        CanAddToPlan = canAdd;
        AddToPlanCommand = new AsyncRelayCommand(() => addToPlan is null ? Task.CompletedTask : addToPlan(), () => canAdd);
    }

    public string Title { get; }
    public string RuleText { get; }
    public bool IsRecommended { get; }
    public string TimeText { get; }
    public string DonePrefix { get; }
    public string DateText { get; }
    public string DetailText { get; }
    public string? AfterCanFlyText { get; }
    public IReadOnlyList<SkillTargetStatShareViewModel> StatShares { get; }
    public IReadOnlyList<SkillTargetFitLineViewModel> FitLines { get; }
    public bool CanAddToPlan { get; }
    public ICommand AddToPlanCommand { get; }

    private static SkillTargetFitLineViewModel _FitLine(string name, SkillImpactStat stat, double free) => free >= 0
        ? new($"✓ {name} fits · {SkillImpactStats.UnsignedAmount(stat, free)} free", true)
        : new($"✗ {name} short {SkillImpactStats.UnsignedAmount(stat, free)}", false);

    private static string _Sp(double sp) => sp >= 1_000_000 ? $"{(sp / 1_000_000).ToString("0.00", Inv)}M"
        : sp >= 1_000 ? $"{(sp / 1_000).ToString("0", Inv)}k"
        : sp.ToString("0", Inv);
}
