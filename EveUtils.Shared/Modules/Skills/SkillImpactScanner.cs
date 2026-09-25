using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>
/// Prices every SDE skill against a fit: a base calculation with the fit-detail's own module states (ET-356 — one
/// pass, no separate propmod-online pass), then one at level V for each candidate skill still below V. A skill
/// "moves" the fit when one of the fifteen <see cref="SkillImpactStat"/> values changes beyond a small epsilon; a
/// mover is priced a second time at its current level + 1. Pure — no UI, no threading of its own, so the caller
/// decides where it runs (off the UI thread, via <c>Task.Run</c>, per the grooming).
/// </summary>
public sealed class SkillImpactScanner(IDogmaCalculator calculator, IDogmaDataAccessor data)
{
    private const double Epsilon = 1e-6;

    // Ship attributes not on DerivedStats — same CCP-stable ids DogmaFitStatsProvider reads for the fit-detail panels.
    private const int LockRangeAttribute = 76;
    private const int ScanResolutionAttribute = 564;
    private static readonly int[] SensorStrengthAttributes = [208, 209, 210, 211];

    public async Task<SkillImpactResult> ScanAsync(
        FitInput baseInput, IReadOnlyDictionary<int, int> trainedLevels, CancellationToken cancellationToken = default)
    {
        var baseValues = _ReadStats(await calculator.CalculateAsync(baseInput, cancellationToken));

        var entries = new List<SkillImpactEntry>();
        foreach (var skillTypeId in data.GetSkillTypeIds())
        {
            var currentLevel = trainedLevels.GetValueOrDefault(skillTypeId);
            if (currentLevel >= 5)
                continue;

            var atFive = _ReadStats(await calculator.CalculateAsync(
                _WithLevel(baseInput, trainedLevels, skillTypeId, 5), cancellationToken));
            var moved = baseValues.Keys.Where(stat => Math.Abs(baseValues[stat] - atFive[stat]) > Epsilon).ToList();
            if (moved.Count == 0)
                continue;

            var nextLevel = currentLevel + 1;
            var atNext = nextLevel == 5
                ? atFive
                : _ReadStats(await calculator.CalculateAsync(
                    _WithLevel(baseInput, trainedLevels, skillTypeId, nextLevel), cancellationToken));

            entries.Add(new SkillImpactEntry(skillTypeId, currentLevel,
                moved.ToDictionary(stat => stat, stat => atNext[stat]),
                moved.ToDictionary(stat => stat, stat => atFive[stat])));
        }

        var maxedMovers = await _FindMaxedMoversAsync(baseInput, trainedLevels, baseValues, entries, cancellationToken);
        return new SkillImpactResult(baseValues, entries, maxedMovers);
    }

    // A stat with no untrained mover reads two different ways in the UI: no skill touches it at all, or every skill
    // that touches it is already trained to V. Distinguishing the two means testing the already-maxed skills too —
    // at level 0 instead of their trained level — but only for the stats that still have no mover, and only over the
    // handful of skills this character actually has at V (not the whole SDE).
    private async Task<IReadOnlySet<SkillImpactStat>> _FindMaxedMoversAsync(FitInput baseInput,
        IReadOnlyDictionary<int, int> trainedLevels, IReadOnlyDictionary<SkillImpactStat, double> baseValues,
        IReadOnlyList<SkillImpactEntry> entries, CancellationToken cancellationToken)
    {
        var stillStats = new HashSet<SkillImpactStat>(baseValues.Keys);
        foreach (var entry in entries)
            stillStats.ExceptWith(entry.AtFive.Keys);
        if (stillStats.Count == 0)
            return stillStats;

        var maxedMovers = new HashSet<SkillImpactStat>();
        foreach (var (skillTypeId, level) in trainedLevels)
        {
            if (level < 5)
                continue;

            var atZero = _ReadStats(await calculator.CalculateAsync(
                _WithLevel(baseInput, trainedLevels, skillTypeId, 0), cancellationToken));
            foreach (var stat in stillStats)
                if (Math.Abs(baseValues[stat] - atZero[stat]) > Epsilon)
                    maxedMovers.Add(stat);
        }
        return maxedMovers;
    }

    private static FitInput _WithLevel(
        FitInput baseInput, IReadOnlyDictionary<int, int> trainedLevels, int skillTypeId, int level)
    {
        var levels = new Dictionary<int, int>(trainedLevels) { [skillTypeId] = level };
        return baseInput with { Skills = SkillSource.From(levels) };
    }

    private static Dictionary<SkillImpactStat, double> _ReadStats(FitResult result)
    {
        var d = result.Derived;
        var stats = new Dictionary<SkillImpactStat, double>
        {
            [SkillImpactStat.Dps] = d.TotalDps,
            [SkillImpactStat.DroneDps] = d.DroneDps,
            [SkillImpactStat.Ehp] = d.Ehp,
            [SkillImpactStat.Capacitor] = d.CapacitorStable
                ? 3600 + d.CapacitorStablePercent
                : Math.Min(d.CapacitorDepletesInSeconds, 3600),
            [SkillImpactStat.Speed] = d.MaxVelocity,
            [SkillImpactStat.AlignTime] = d.AlignTime,
            [SkillImpactStat.Signature] = d.SignatureRadius,
            [SkillImpactStat.LockRange] = result.ShipAttribute(LockRangeAttribute),
            [SkillImpactStat.ScanResolution] = result.ShipAttribute(ScanResolutionAttribute),
            [SkillImpactStat.SensorStrength] = SensorStrengthAttributes.Max(result.ShipAttribute),
            [SkillImpactStat.FreeCpu] = d.CpuOutput - d.CpuUsed,
            [SkillImpactStat.FreePg] = d.PowerOutput - d.PowerUsed,
        };

        // Optimal/Falloff/Tracking come off the highest-DPS turret or drone contribution; a fit with only missiles
        // (or no weapons at all) qualifies for none of the three, so the caller greys them out as "no turret or
        // drone weapons" rather than reporting them un-moved.
        var weapon = result.Contributions
            .Where(contribution => contribution.Kind is ModuleContributionKind.Turret or ModuleContributionKind.Drone)
            .OrderByDescending(contribution => contribution.Dps)
            .FirstOrDefault();
        if (weapon is not null)
        {
            stats[SkillImpactStat.Optimal] = weapon.OptimalRange;
            stats[SkillImpactStat.Falloff] = weapon.FalloffRange;
            stats[SkillImpactStat.Tracking] = weapon.TrackingSpeed;
        }
        return stats;
    }
}
