using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One point on the curve: cumulative training time and score at that point, for the X/Y of a line chart.</summary>
public sealed record SkillTargetCurvePointViewModel(double CumulativeHours, double ScorePercent);

/// <summary>
/// The greedy training curve (ET-357 D10) from can fly to max: score against cumulative training time, with the
/// point where the curve first reaches the optimal ±III set and the point that reaches 100% (always the last point,
/// since the curve's candidate pool is exactly the max card's own movers).
/// </summary>
public sealed class SkillTargetCurveViewModel(SkillTargetCurve curve, IReadOnlyList<SkillImpactStatChipViewModel> chosen)
{
    public IReadOnlyList<SkillTargetCurvePointViewModel> Points { get; } = curve.Points
        .Select(point => new SkillTargetCurvePointViewModel(point.CumulativeTime.TotalHours, point.Score * 100)).ToList();

    public int OptimalPointIndex { get; } = curve.OptimalPointIndex;
    public int MaxPointIndex { get; } = curve.MaxPointIndex;

    /// <summary>The first point at 100%: levels after it still belong to max but add nothing to the chosen stats.</summary>
    public int FullPointIndex => Points.Count == 0 ? -1
        : Points.Select((point, index) => (Point: point, Index: index))
            .FirstOrDefault(pair => pair.Point.ScorePercent >= 99.95, (Point: Points[^1], Index: Points.Count - 1)).Index;

    public string Title => $"COMBINED GAIN · {string.Join(", ", chosen.Select(chip => chip.Label)).ToUpperInvariant()} · AGAINST TRAINING TIME";

    public string OptimalLabel => OptimalPointIndex < 0
        ? "optimal · reached at can fly"
        : $"optimal · {Points[OptimalPointIndex].ScorePercent:0}% after {_Time(Points[OptimalPointIndex].CumulativeHours)}";

    public string EndLabel => FullPointIndex < 0
        ? "nothing left to gain"
        : $"{Points[FullPointIndex].ScorePercent:0}% after {_Time(Points[FullPointIndex].CumulativeHours)}";

    public string Caption => chosen.Count > 1
        ? $"Each stat counts as a share of what skills can add to it, so {chosen[1].RuleName} weighs as much as {chosen[0].RuleName}, "
          + "whatever its unit. Each step trains the level with the most combined gain per hour."
        : $"The share of what skills can add to {chosen.FirstOrDefault()?.RuleName}, from can fly. Each step trains the level with the most gain per hour.";

    private static string _Time(double hours) => EveDurationFormatter.Format(TimeSpan.FromHours(hours));
}
