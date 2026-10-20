using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>
/// The expected yield of one activity out of its samples (ET-513) — pure, no IO, the same for the own runs and a
/// server's outcomes. No trimmed mean: in a skewed distribution it is neither the typical run nor the long-run income,
/// and below 10 runs a 10/10 trim cuts nothing at all (Depot Research/earnings-stats). The figures are a weighted
/// median with its band, the real worst and best run, and the mean only once there are enough runs to carry it.
/// </summary>
public static class EarningsStatistics
{
    /// <summary>How many of the best and the worst activities the figures name (ET-517).</summary>
    public const int ExtremeRunCount = 3;

    /// <param name="unitPrice">Today's price per type; null counts the loot line as 0 and reports the type unpriced.</param>
    /// <param name="nowUtc">The end of the window.</param>
    /// <param name="window">The window the figures are labelled with unless the fallback inside takes over: for a caller
    /// that already chose the longer window itself by reading its samples over it.</param>
    public static EarningsFiguresDto Compute(IReadOnlyList<EarningsSample> samples, Func<int, decimal?> unitPrice,
        EarningsPolicy policy, DateTime nowUtc, EarningsWindow window = EarningsWindow.Recent)
    {
        DateTime sinceUtc = nowUtc - policy.Window;
        IReadOnlyList<(EarningsSample Sample, double Weight)> counted = _Weighted(_Counted(samples, sinceUtc, policy), policy);
        if (counted.Sum(entry => entry.Weight) < policy.MinimumRuns && policy.FallbackWindow is { } fallback)
        {
            window = EarningsWindow.Extended;
            sinceUtc = nowUtc - fallback;
            counted = _Weighted(_Counted(samples, sinceUtc, policy), policy);
        }

        double effective = counted.Sum(entry => entry.Weight);
        bool enough = effective >= policy.MinimumRuns;
        bool deciles = enough && effective >= policy.DecileBandRuns;
        // Ascending by yield, the earliest first among equals, so the worst and the best run are always the same ones.
        (EarningsSample Sample, decimal Yield, double Weight)[] valued = enough
            ? [.. counted
                .Select(entry => (entry.Sample, Yield: YieldOf(entry.Sample, unitPrice), entry.Weight))
                .OrderBy(entry => entry.Yield)
                .ThenBy(entry => entry.Sample.CompletedAtUtc)]
            : [];
        (decimal Value, double Weight)[] yields = [.. valued.Select(entry => (entry.Yield, entry.Weight))];
        decimal? typical = enough ? WeightedQuantile.Of(yields, 0.5) : null;
        return new EarningsFiguresDto
        {
            Confidence = deciles ? EarningsConfidence.Normal : enough ? EarningsConfidence.Low : EarningsConfidence.None,
            Window = window,
            SinceUtc = sinceUtc,
            RunCount = counted.Count,
            EffectiveRunCount = effective,
            PilotCount = counted.Select(entry => entry.Sample.PilotBucket).Distinct().Count(),
            ExcludedRunCount = samples.Count(sample => sample.CompletedAtUtc >= sinceUtc && _IsTooShort(sample, policy)),
            WithoutLootCount = _InWindow(samples, sinceUtc, policy).Count(sample => sample.LootBasis is LootBasis.None),
            IncompleteLootCount = counted.Count(entry => entry.Sample.OpenUnrecognisedLootCount > 0),
            UnpricedItemCount = counted
                .SelectMany(entry => entry.Sample.LootGained.Concat(entry.Sample.LootLost).Concat(entry.Sample.Mined))
                .Select(line => line.TypeId)
                .Distinct()
                .Count(typeId => unitPrice(typeId) is null),
            Band = deciles ? EarningsBand.Deciles : enough ? EarningsBand.Quartiles : EarningsBand.None,
            Typical = typical,
            P10 = deciles ? WeightedQuantile.Of(yields, 0.1) : null,
            P25 = enough ? WeightedQuantile.Of(yields, 0.25) : null,
            P75 = enough ? WeightedQuantile.Of(yields, 0.75) : null,
            P90 = deciles ? WeightedQuantile.Of(yields, 0.9) : null,
            LongRunAverage = enough && effective >= policy.LongRunAverageRuns
                ? valued.Sum(entry => entry.Yield * (decimal)entry.Weight) / (decimal)effective
                : null,
            JackpotShare = typical > 0
                ? valued.Where(entry => entry.Yield > typical * policy.JackpotMultiple).Sum(entry => entry.Weight) / effective
                : null,
            TypicalIskPerHour = enough ? _TypicalPerHour(valued, policy) : null,
            Worst = enough ? _Run(valued[0]) : null,
            Best = enough ? _Run(valued[^1]) : null,
            WorstRuns = [.. valued.Take(ExtremeRunCount).Select(_Run)],
            BestRuns = [.. Enumerable.Reverse(valued).Take(ExtremeRunCount).Select(_Run)],
            Yields = [.. valued.Select(entry => new EarningsYieldDto { YieldIsk = entry.Yield, Weight = entry.Weight })]
        };
    }

    /// <summary>Gross yield at today's price: the fixed ISK plus the loot gained less the loot lost, plus the ore that
    /// belongs to the site.</summary>
    public static decimal YieldOf(EarningsSample sample, Func<int, decimal?> unitPrice) =>
        sample.FixedIsk
        + _Value(sample.LootGained, unitPrice)
        - _Value(sample.LootLost, unitPrice)
        + _Value(sample.Mined, unitPrice);

    private static decimal _Value(IEnumerable<EarningsLootLine> lines, Func<int, decimal?> unitPrice) =>
        lines.Sum(line => unitPrice(line.TypeId).GetValueOrDefault() * line.Quantity);

    /// <summary>The per-pilot cap weight of each sample, in order, as <see cref="Compute"/> weighs them: min(1, C / the
    /// pilot's counted samples), and 0 for a sample the policy leaves out. For a caller that hands the weights on
    /// without the pilot bucket they were counted by — a server's anonymous outcomes (ET-514).</summary>
    public static IReadOnlyList<double> CapWeights(IReadOnlyList<EarningsSample> samples, EarningsPolicy policy)
    {
        Dictionary<string, int> runsByPilot = samples
            .Where(sample => _IsCounted(sample, policy))
            .GroupBy(sample => sample.PilotBucket, StringComparer.Ordinal)
            .ToDictionary(pilot => pilot.Key, pilot => pilot.Count(), StringComparer.Ordinal);
        return [.. samples.Select(sample => _IsCounted(sample, policy)
            ? Math.Min(1d, (double)policy.RunsPerPilotCap / runsByPilot[sample.PilotBucket])
            : 0d)];
    }

    private static IEnumerable<EarningsSample> _Counted(IEnumerable<EarningsSample> samples, DateTime sinceUtc,
        EarningsPolicy policy) =>
        samples.Where(sample => sample.CompletedAtUtc >= sinceUtc && _IsCounted(sample, policy));

    private static bool _IsCounted(EarningsSample sample, EarningsPolicy policy) =>
        !_IsTooShort(sample, policy)
        && (policy.RunsWithoutLoot is RunsWithoutLoot.Counted || sample.LootBasis is not LootBasis.None);

    private static IEnumerable<EarningsSample> _InWindow(IEnumerable<EarningsSample> samples, DateTime sinceUtc,
        EarningsPolicy policy) =>
        samples.Where(sample => sample.CompletedAtUtc >= sinceUtc && !_IsTooShort(sample, policy));

    private static bool _IsTooShort(EarningsSample sample, EarningsPolicy policy) =>
        sample.DurationSeconds is { } seconds && seconds < policy.MinimumDuration.TotalSeconds;

    // w = min(1, C / runs of that pilot in the window): a pilot's runs together never weigh more than C.
    private static IReadOnlyList<(EarningsSample Sample, double Weight)> _Weighted(IEnumerable<EarningsSample> samples,
        EarningsPolicy policy)
    {
        EarningsSample[] all = [.. samples];
        Dictionary<string, int> runsByPilot = all
            .GroupBy(sample => sample.PilotBucket, StringComparer.Ordinal)
            .ToDictionary(pilot => pilot.Key, pilot => pilot.Count(), StringComparer.Ordinal);
        return [.. all.Select(sample => (sample,
            sample.CapWeight ?? Math.Min(1d, (double)policy.RunsPerPilotCap / runsByPilot[sample.PilotBucket])))];
    }

    private static decimal? _TypicalPerHour(IEnumerable<(EarningsSample Sample, decimal Yield, double Weight)> valued,
        EarningsPolicy policy)
    {
        (decimal Value, double Weight)[] perHour = [.. valued
            .Where(entry => entry.Sample.DurationSeconds > 0)
            .Select(entry => (Value: entry.Yield * 3600m / entry.Sample.DurationSeconds.GetValueOrDefault(), entry.Weight))
            .OrderBy(entry => entry.Value)];
        return perHour.Sum(entry => entry.Weight) >= policy.MinimumRuns ? WeightedQuantile.Of(perHour, 0.5) : null;
    }

    private static EarningsRunDto _Run((EarningsSample Sample, decimal Yield, double Weight) entry) => new()
    {
        CompletedAtUtc = entry.Sample.CompletedAtUtc,
        YieldIsk = entry.Yield,
        DurationSeconds = entry.Sample.DurationSeconds,
        FleetSize = entry.Sample.FleetSize,
        ActivityRef = entry.Sample.ActivityRef
    };
}
