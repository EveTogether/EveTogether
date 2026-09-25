using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// The three skill goals for a fit (ET-357): can fly (SDE requirements plus a greedy fitting-skill fix for any CPU/PG
/// overload), optimal (every mover of a chosen stat at ≥ III) and max (at V), plus the greedy training curve between
/// can fly and max. Pure — no UI, no threading of its own, same rule as <see cref="SkillImpactScanner"/>, whose scan
/// result supplies both the fitting-skill and the stat-mover candidate pools so this never re-scans the whole SDE.
/// </summary>
public sealed class SkillTargetsCalculator(
    IDogmaCalculator calculator, IFitValidator validator, SkillTrainingEstimator estimator, CharacterAttributeSet attributes)
{
    private const double Epsilon = 1e-6;
    private const int OptimalMinLevel = 3;
    private const int MaxLevel = 5;
    private static readonly HashSet<SkillImpactStat> LowerIsBetter = [SkillImpactStat.AlignTime, SkillImpactStat.Signature];

    public async Task<SkillTargetsResult> CalculateAsync(FitInput baseInput, IReadOnlyDictionary<int, int> trained,
        SkillImpactResult scan, IReadOnlyList<SkillImpactStat> selectedStats, CancellationToken cancellationToken = default)
    {
        var canFlyBase = _Merge(trained, validator.SkillRequirements(_SeedTypeIds(baseInput), null, trained));

        // Only CPU/PG are skill-fixable (calibration, drone bay and bandwidth never move with a skill level in this
        // engine), so the greedy fit-check candidate pool is narrowed to the scan's own FreeCpu/FreePg movers.
        var fittingMovers = scan.Entries
            .Where(entry => entry.AtFive.ContainsKey(SkillImpactStat.FreeCpu) || entry.AtFive.ContainsKey(SkillImpactStat.FreePg))
            .Select(entry => entry.SkillTypeId).ToList();
        var (canFlyLevels, shortfalls) =
            await _ResolveFitCheckAsync(baseInput, canFlyBase, fittingMovers, cancellationToken);

        var statMovers = scan.Entries.Where(entry => selectedStats.Any(entry.AtFive.ContainsKey))
            .Select(entry => entry.SkillTypeId).ToList();
        var optimalTargets = statMovers.Select(skillTypeId => new SkillMinimum(skillTypeId, OptimalMinLevel)).ToList();
        var maxTargets = statMovers.Select(skillTypeId => new SkillMinimum(skillTypeId, MaxLevel)).ToList();
        // A skill's own prerequisites are fixed by its type, not by the level being trained to — the III and V
        // closures agree on every prerequisite, and differ only in the movers' own target level (AC1: can fly ⊆
        // optimal ⊆ max, since _Merge only ever raises a level already ≥ its input, never lowers one).
        var optimalLevels = _Merge(canFlyLevels, validator.SkillRequirements([], optimalTargets, canFlyLevels));
        var maxLevels = _Merge(canFlyLevels, validator.SkillRequirements([], maxTargets, canFlyLevels));

        var canFlyValues = await _StatsAtAsync(baseInput, canFlyLevels, cancellationToken);
        var maxValues = await _StatsAtAsync(baseInput, maxLevels, cancellationToken);
        var optimalValues = await _StatsAtAsync(baseInput, optimalLevels, cancellationToken);

        // A stat whose max equals its can-fly value has nothing this fit's skills can give it (e.g. a stat with no
        // untrained mover at all) — excluded from the average rather than silently contributing a flat 0.
        var qualifyingStats = selectedStats
            .Where(stat => Math.Abs(maxValues[stat] - canFlyValues[stat]) > Epsilon).ToList();
        double Score(IReadOnlyDictionary<SkillImpactStat, double> values) => qualifyingStats.Count == 0 ? 0
            : qualifyingStats.Average(stat =>
                StatShare.Compute(canFlyValues[stat], values[stat], maxValues[stat], LowerIsBetter.Contains(stat)));

        var canFlyGoal = _BuildGoal(SkillTargetGoalKind.CanFly, trained, canFlyLevels, canFlyValues,
            canFlyValues, maxValues, selectedStats, qualifyingStats, shortfalls);
        var optimalGoal = _BuildGoal(SkillTargetGoalKind.Optimal, trained, optimalLevels, optimalValues,
            canFlyValues, maxValues, selectedStats, qualifyingStats, []);
        var maxGoal = _BuildGoal(SkillTargetGoalKind.Max, trained, maxLevels, maxValues,
            canFlyValues, maxValues, selectedStats, qualifyingStats, []);

        var curve = await _BuildCurveAsync(baseInput, canFlyLevels, optimalLevels, statMovers, Score,
            canFlyValues, maxValues, selectedStats, cancellationToken);
        return new SkillTargetsResult(canFlyGoal, optimalGoal, maxGoal, curve);
    }

    // Greedy: resolve any CPU/PG overload by repeatedly training the fitting-mover level (prerequisites bundled)
    // with the highest freed capacity per hour, until nothing is left short or every mover is at V.
    private async Task<(Dictionary<int, int> Levels, IReadOnlyList<SkillTargetResourceShortfall> Shortfalls)> _ResolveFitCheckAsync(
        FitInput baseInput, IReadOnlyDictionary<int, int> startLevels, IReadOnlyList<int> fittingMovers,
        CancellationToken cancellationToken)
    {
        var levels = new Dictionary<int, int>(startLevels);
        var stats = await _StatsAtAsync(baseInput, levels, cancellationToken);
        while (_ShortfallScore(stats) < 0)
        {
            var step = await _BestStepAsync(baseInput, levels, fittingMovers, _ShortfallScore, _ShortfallScore(stats), cancellationToken);
            if (step is null)
                break;   // every fitting mover already at V, and still short

            _ApplyBundle(levels, step.Value.Bundle);
            stats = await _StatsAtAsync(baseInput, levels, cancellationToken);
        }

        var shortfalls = new List<SkillTargetResourceShortfall>();
        if (stats[SkillImpactStat.FreeCpu] < 0)
            shortfalls.Add(new SkillTargetResourceShortfall(SkillImpactStat.FreeCpu, -stats[SkillImpactStat.FreeCpu]));
        if (stats[SkillImpactStat.FreePg] < 0)
            shortfalls.Add(new SkillTargetResourceShortfall(SkillImpactStat.FreePg, -stats[SkillImpactStat.FreePg]));
        return (levels, shortfalls);
    }

    // D10: greedy from can fly, one level at a time (prerequisites bundled), always taking the candidate with the
    // highest Δscore per hour. Stops once every stat-mover is at V — the same level set as the max card, so the last
    // point's score is always the curve's own 100%.
    private async Task<SkillTargetCurve> _BuildCurveAsync(FitInput baseInput, IReadOnlyDictionary<int, int> canFlyLevels,
        IReadOnlyDictionary<int, int> optimalLevels, IReadOnlyList<int> statMovers,
        Func<IReadOnlyDictionary<SkillImpactStat, double>, double> scoreOf,
        IReadOnlyDictionary<SkillImpactStat, double> canFlyValues, IReadOnlyDictionary<SkillImpactStat, double> maxValues,
        IReadOnlyList<SkillImpactStat> selectedStats, CancellationToken cancellationToken)
    {
        var levels = new Dictionary<int, int>(canFlyLevels);
        var points = new List<SkillTargetCurvePoint>();
        var cumulativeTime = TimeSpan.Zero;
        double cumulativeSp = 0;
        double score = scoreOf(await _StatsAtAsync(baseInput, levels, cancellationToken));
        int optimalIndex = _Satisfies(levels, optimalLevels) ? -1 : int.MinValue;

        while (true)
        {
            var step = await _BestStepAsync(baseInput, levels, statMovers, scoreOf, score, cancellationToken);
            if (step is null)
                break;

            _ApplyBundle(levels, step.Value.Bundle);
            cumulativeTime += step.Value.Time;
            cumulativeSp += step.Value.Bundle.Sum(gap => estimator.Estimate(gap.SkillTypeId, gap.CurrentLevel, gap.RequiredLevel, attributes).SkillPointsRequired);
            score += step.Value.Gain;
            // Per-stat shares alongside the combined score (A5: proves a small stat's own progress, not just the
            // average) — read off the same afterStats the winning step already computed, no extra engine call.
            var shares = selectedStats.ToDictionary(stat => stat,
                stat => StatShare.Compute(canFlyValues[stat], step.Value.AfterStats[stat], maxValues[stat], LowerIsBetter.Contains(stat)));
            points.Add(new SkillTargetCurvePoint(step.Value.SkillTypeId, step.Value.Level, cumulativeSp, cumulativeTime, score, shares));

            if (optimalIndex == int.MinValue && _Satisfies(levels, optimalLevels))
                optimalIndex = points.Count - 1;
        }

        if (optimalIndex == int.MinValue)
            optimalIndex = points.Count - 1;   // never reached mid-walk (e.g. no stat movers at all) — clamp to the end
        return new SkillTargetCurve(points, optimalIndex, points.Count - 1);
    }

    private readonly record struct GreedyStep(int SkillTypeId, int Level, IReadOnlyList<SkillGap> Bundle, TimeSpan Time,
        double Gain, double PerHour, IReadOnlyDictionary<SkillImpactStat, double> AfterStats);

    // The candidate whose own next level (prerequisites bundled as one pick, D10) yields the highest gain per hour of
    // training, judged by `scoreOf` against `currentScore`. Null once every candidate is at V.
    private async Task<GreedyStep?> _BestStepAsync(FitInput baseInput, IReadOnlyDictionary<int, int> currentLevels,
        IReadOnlyList<int> candidates, Func<IReadOnlyDictionary<SkillImpactStat, double>, double> scoreOf,
        double currentScore, CancellationToken cancellationToken)
    {
        GreedyStep? best = null;
        foreach (var skillTypeId in candidates)
        {
            int level = currentLevels.GetValueOrDefault(skillTypeId);
            if (level >= MaxLevel)
                continue;

            var bundle = validator.SkillRequirements([], [new SkillMinimum(skillTypeId, level + 1)], currentLevels);
            if (bundle.Count == 0)
                continue;

            var time = _BundleTime(bundle);
            var afterStats = await _StatsAtAsync(baseInput, _WithBundle(currentLevels, bundle), cancellationToken);
            var gain = scoreOf(afterStats) - currentScore;
            double perHour = time.TotalHours > 0 ? gain / time.TotalHours : double.PositiveInfinity;

            if (best is null || perHour > best.Value.PerHour)
                best = new GreedyStep(skillTypeId, level + 1, bundle, time, gain, perHour, afterStats);
        }
        return best;
    }

    private async Task<IReadOnlyDictionary<SkillImpactStat, double>> _StatsAtAsync(
        FitInput baseInput, IReadOnlyDictionary<int, int> levels, CancellationToken cancellationToken) =>
        SkillImpactScanner.ReadStats(await calculator.CalculateAsync(
            baseInput with { Skills = SkillSource.From(levels) }, cancellationToken));

    private TimeSpan _BundleTime(IReadOnlyList<SkillGap> bundle)
    {
        var total = TimeSpan.Zero;
        foreach (var gap in bundle)
            total += estimator.Estimate(gap.SkillTypeId, gap.CurrentLevel, gap.RequiredLevel, attributes).TrainingTime;
        return total;
    }

    private static Dictionary<int, int> _WithBundle(IReadOnlyDictionary<int, int> levels, IReadOnlyList<SkillGap> bundle)
    {
        var next = new Dictionary<int, int>(levels);
        _ApplyBundle(next, bundle);
        return next;
    }

    private static void _ApplyBundle(Dictionary<int, int> levels, IReadOnlyList<SkillGap> bundle)
    {
        foreach (var gap in bundle)
            levels[gap.SkillTypeId] = gap.RequiredLevel;
    }

    private static Dictionary<int, int> _Merge(IReadOnlyDictionary<int, int> baseLevels, IReadOnlyList<SkillGap> gaps)
    {
        var merged = new Dictionary<int, int>(baseLevels);
        foreach (var gap in gaps)
            merged[gap.SkillTypeId] = gap.RequiredLevel;
        return merged;
    }

    private static bool _Satisfies(IReadOnlyDictionary<int, int> levels, IReadOnlyDictionary<int, int> targets) =>
        targets.All(target => levels.GetValueOrDefault(target.Key) >= target.Value);

    private static double _ShortfallScore(IReadOnlyDictionary<SkillImpactStat, double> stats) =>
        Math.Min(0, stats[SkillImpactStat.FreeCpu]) + Math.Min(0, stats[SkillImpactStat.FreePg]);

    private static IReadOnlyList<int> _SeedTypeIds(FitInput input)
    {
        var ids = new List<int> { input.ShipTypeId };
        foreach (var module in input.Modules)
        {
            ids.Add(module.TypeId);
            if (module.ChargeTypeId is { } charge)
                ids.Add(charge);
        }
        if (input.Drones is { } drones)
            ids.AddRange(drones.Select(drone => drone.TypeId));
        return ids;
    }

    private SkillTargetGoal _BuildGoal(SkillTargetGoalKind kind, IReadOnlyDictionary<int, int> trained,
        IReadOnlyDictionary<int, int> targetLevels, IReadOnlyDictionary<SkillImpactStat, double> values,
        IReadOnlyDictionary<SkillImpactStat, double> canFlyValues, IReadOnlyDictionary<SkillImpactStat, double> maxValues,
        IReadOnlyList<SkillImpactStat> selectedStats, IReadOnlyList<SkillImpactStat> qualifyingStats,
        IReadOnlyList<SkillTargetResourceShortfall> shortfalls)
    {
        var levels = new List<SkillTargetLevel>();
        double skillPoints = 0;
        var trainingTime = TimeSpan.Zero;
        foreach (var (skillTypeId, level) in targetLevels)
        {
            int current = trained.GetValueOrDefault(skillTypeId);
            if (level <= current)
                continue;   // AC6: only what still needs training — never a level already trained

            levels.Add(new SkillTargetLevel(skillTypeId, level));
            var estimate = estimator.Estimate(skillTypeId, current, level, attributes);
            skillPoints += estimate.SkillPointsRequired;
            trainingTime += estimate.TrainingTime;
        }

        var shares = selectedStats.ToDictionary(stat => stat,
            stat => StatShare.Compute(canFlyValues[stat], values[stat], maxValues[stat], LowerIsBetter.Contains(stat)));
        double score = qualifyingStats.Count == 0 ? 0 : qualifyingStats.Average(stat => shares[stat]);

        return new SkillTargetGoal(kind, levels, skillPoints, trainingTime, shares, score, shortfalls);
    }
}

public enum SkillTargetGoalKind { CanFly, Optimal, Max }

public sealed record SkillTargetLevel(int SkillTypeId, int Level);

/// <summary>A resource the can-fly card still can't afford at level V on every fitting mover — <see cref="Shortfall"/>
/// is the positive amount still over budget ("This fit does not fit at any skill level").</summary>
public sealed record SkillTargetResourceShortfall(SkillImpactStat Resource, double Shortfall);

public sealed record SkillTargetGoal(
    SkillTargetGoalKind Kind,
    IReadOnlyList<SkillTargetLevel> Levels,
    double SkillPoints,
    TimeSpan TrainingTime,
    IReadOnlyDictionary<SkillImpactStat, double> StatShares,
    double Score,
    IReadOnlyList<SkillTargetResourceShortfall> Shortfalls)
{
    public bool Fits => Shortfalls.Count == 0;
}

public sealed record SkillTargetCurvePoint(int SkillTypeId, int Level, double SkillPoints, TimeSpan CumulativeTime,
    double Score, IReadOnlyDictionary<SkillImpactStat, double> StatShares);

/// <param name="OptimalPointIndex">-1 when the optimal set is already satisfied at can fly, before any curve step.</param>
public sealed record SkillTargetCurve(
    IReadOnlyList<SkillTargetCurvePoint> Points, int OptimalPointIndex, int MaxPointIndex);

public sealed record SkillTargetsResult(SkillTargetGoal CanFly, SkillTargetGoal Optimal, SkillTargetGoal Max, SkillTargetCurve Curve);
