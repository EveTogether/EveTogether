using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One point on the curve: cumulative training time and score at that point, for the X/Y of a line chart.</summary>
public sealed record SkillTargetCurvePointViewModel(double CumulativeHours, double ScorePercent);

/// <summary>
/// The greedy training curve (ET-357 D10) from can fly to max: score against cumulative training time, with the
/// point where the curve first reaches the optimal ±III set and the point that reaches 100% (always the last point,
/// since the curve's candidate pool is exactly the max card's own movers).
/// </summary>
public sealed class SkillTargetCurveViewModel(SkillTargetCurve curve)
{
    public IReadOnlyList<SkillTargetCurvePointViewModel> Points { get; } = curve.Points
        .Select(point => new SkillTargetCurvePointViewModel(point.CumulativeTime.TotalHours, point.Score * 100)).ToList();

    public int OptimalPointIndex { get; } = curve.OptimalPointIndex;
    public int MaxPointIndex { get; } = curve.MaxPointIndex;

    public string StepsText => $"{Points.Count} step{(Points.Count == 1 ? "" : "s")}";

    public string OptimalMarkerText => OptimalPointIndex < 0
        ? "optimal reached at can fly"
        : $"optimal at {Points[OptimalPointIndex].CumulativeHours.ToString("0", CultureInfo.InvariantCulture)}h";

    public string HundredPercentMarkerText => MaxPointIndex < 0
        ? "—"
        : $"100% at {Points[MaxPointIndex].CumulativeHours.ToString("0", CultureInfo.InvariantCulture)}h";
}
