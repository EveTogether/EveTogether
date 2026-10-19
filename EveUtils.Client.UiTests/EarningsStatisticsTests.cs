using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-513: the earnings statistic. Every expected figure is worked out by hand with the midpoint rule: each
/// run stands at the middle of its own weight, so with n equal weights run k sits at (k - 0.5) / n.</summary>
public sealed class EarningsStatisticsTests
{
    private static readonly DateTime NowUtc = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly EarningsPolicy AnyCount = EarningsPolicy.Default with { MinimumRuns = 1 };

    [Theory]
    [InlineData(new[] { 7d }, 7d, 7d, 7d)]
    [InlineData(new[] { 1d, 2d, 3d, 4d }, 2.5d, 1.5d, 3.5d)]
    [InlineData(new[] { 1d, 2d, 3d, 4d, 5d }, 3d, 1.75d, 4.25d)]
    [InlineData(new[] { 5d, 5d, 5d, 10d, 10d }, 5d, 5d, 10d)]
    [InlineData(new[] { 4d, 4d, 4d, 4d }, 4d, 4d, 4d)]
    public void Compute_EqualWeights_GivesTheMidpointQuantiles(double[] yields, double median, double p25, double p75)
    {
        EarningsFiguresDto figures = EarningsStatistics.Compute(
            [.. yields.Select((yield, index) => _Sample($"pilot-{index}", (decimal)yield))], _ => null, AnyCount, NowUtc);

        Assert.Equal((decimal)median, figures.Typical);
        Assert.Equal((decimal)p25, figures.P25);
        Assert.Equal((decimal)p75, figures.P75);
    }

    /// <summary>The cap's own case: 200 runs at 100 count as 10 runs against 5 pilots' 15 runs at 10. Unweighted the
    /// farmer is the median (100); weighted the 10s fill the first 15 of 25, so the median at 12.5 is a 10.</summary>
    [Fact]
    public void Compute_FarmerWithManyRuns_IsCappedAtTenRunsOfWeight()
    {
        EarningsSample[] samples =
        [
            .. Enumerable.Range(0, 200).Select(_ => _Sample("farmer", 100m)),
            .. Enumerable.Range(0, 15).Select(index => _Sample($"pilot-{index % 5}", 10m))
        ];

        EarningsFiguresDto figures = EarningsStatistics.Compute(samples, _ => null, EarningsPolicy.Default, NowUtc);

        Assert.Equal(215, figures.RunCount);
        Assert.Equal(25d, figures.EffectiveRunCount, 9);
        Assert.Equal(6, figures.PilotCount);
        Assert.Equal(10m, figures.Typical);
        Assert.Equal(100m, figures.P90);
    }

    [Theory]
    [InlineData(4, EarningsConfidence.None, EarningsBand.None, false, false, false)]
    [InlineData(5, EarningsConfidence.Low, EarningsBand.Quartiles, true, false, false)]
    [InlineData(19, EarningsConfidence.Low, EarningsBand.Quartiles, true, false, false)]
    [InlineData(20, EarningsConfidence.Normal, EarningsBand.Deciles, true, true, false)]
    [InlineData(30, EarningsConfidence.Normal, EarningsBand.Deciles, true, true, true)]
    public void Compute_EffectiveRunCount_DecidesWhichFiguresAreGiven(int runs, EarningsConfidence confidence,
        EarningsBand band, bool hasTypical, bool hasDeciles, bool hasLongRunAverage)
    {
        EarningsFiguresDto figures = EarningsStatistics.Compute(
            [.. Enumerable.Range(0, runs).Select(index => _Sample($"pilot-{index}", 1_000_000m))], _ => null,
            EarningsPolicy.Default, NowUtc);

        Assert.Equal(runs, figures.RunCount);
        Assert.Equal(confidence, figures.Confidence);
        Assert.Equal(band, figures.Band);
        Assert.Equal(hasTypical, figures.Typical is not null);
        Assert.Equal(hasTypical, figures.P25 is not null && figures.Worst is not null && figures.Best is not null);
        Assert.Equal(hasDeciles, figures.P10 is not null && figures.P90 is not null);
        Assert.Equal(hasLongRunAverage, figures.LongRunAverage is not null);
    }

    /// <summary>A skewed site: 27 ordinary runs and 3 jackpots. The typical run stays the ordinary one, the long-run
    /// average carries the jackpots ((27 × 40 + 3 × 400) / 30 = 76), and worst and best are the real runs.</summary>
    [Fact]
    public void Compute_SkewedDistribution_KeepsTypicalOnTheOrdinaryRun_AndTheJackpotsInTheAverage()
    {
        EarningsSample[] samples =
        [
            .. Enumerable.Range(0, 27).Select(index => _Sample($"pilot-{index}", 40m, NowUtc.AddDays(-index - 1))),
            .. Enumerable.Range(0, 3).Select(index => _Sample($"lucky-{index}", 400m, NowUtc.AddDays(-index - 30)))
        ];

        EarningsFiguresDto figures = EarningsStatistics.Compute(samples, _ => null, EarningsPolicy.Default, NowUtc);

        Assert.Equal(40m, figures.Typical);
        Assert.Equal(76m, figures.LongRunAverage);
        Assert.Equal(0.1d, figures.JackpotShare.GetValueOrDefault(), 9);
        Assert.Equal(new EarningsRunDto { CompletedAtUtc = NowUtc.AddDays(-27), YieldIsk = 40m, DurationSeconds = 600, FleetSize = 1 },
            figures.Worst, _SameRun);
        Assert.Equal(400m, figures.Best?.YieldIsk);
    }

    /// <summary>Why there is no trimmed mean (ET-512): with fewer than 10 runs a 10/10 trim cuts floor(0.1 × n) = 0
    /// runs from each end, so the "robust" figure is the plain mean, pulled up by the one jackpot.</summary>
    [Fact]
    public void TrimmedMean_BelowTenRuns_IsThePlainMean_WhileTypicalIgnoresTheJackpot()
    {
        decimal[] yields = [10m, 12m, 14m, 15m, 16m, 18m, 20m, 22m, 300m];
        int cut = yields.Length / 10;

        decimal trimmed = yields.Order().Skip(cut).Take(yields.Length - 2 * cut).Average();
        EarningsFiguresDto figures = EarningsStatistics.Compute(
            [.. yields.Select((yield, index) => _Sample($"pilot-{index}", yield))], _ => null, EarningsPolicy.Default, NowUtc);

        Assert.Equal(yields.Average(), trimmed);
        Assert.Equal(16m, figures.Typical);
    }

    [Theory]
    [InlineData(6, 4, EarningsWindow.Recent, 6)]
    [InlineData(3, 4, EarningsWindow.Extended, 7)]
    [InlineData(1, 1, EarningsWindow.Extended, 2)]
    public void Compute_TooFewRecentRuns_FallsBackToTheYear_AndNeverReachesBeyondIt(int recentRuns, int olderRuns,
        EarningsWindow window, int runCount)
    {
        EarningsSample[] samples =
        [
            .. Enumerable.Range(0, recentRuns).Select(index => _Sample($"pilot-{index}", 10m, NowUtc.AddDays(-30))),
            .. Enumerable.Range(0, olderRuns).Select(index => _Sample($"older-{index}", 10m, NowUtc.AddDays(-200))),
            _Sample("ancient", 10m, NowUtc.AddDays(-400))
        ];

        EarningsFiguresDto figures = EarningsStatistics.Compute(samples, _ => null, EarningsPolicy.Default, NowUtc);

        Assert.Equal(window, figures.Window);
        Assert.Equal(runCount, figures.RunCount);
    }

    /// <summary>A run given up on after 30 s is left out and counted as such; a run without a stop counts per run but
    /// not per hour. ISK/hour is the median of each timed run's own rate: 5 × 10 per hour, not total over time.</summary>
    [Fact]
    public void Compute_ShortRunIsExcluded_AndARunWithoutAStopCountsPerRunOnly()
    {
        EarningsSample[] samples =
        [
            .. Enumerable.Range(0, 5).Select(index => _Sample($"pilot-{index}", 10m) with { DurationSeconds = 3600 }),
            _Sample("no-stop", 1000m) with { DurationSeconds = null },
            _Sample("gave-up", 0m) with { DurationSeconds = 30 }
        ];

        EarningsFiguresDto figures = EarningsStatistics.Compute(samples, _ => null, EarningsPolicy.Default, NowUtc);

        Assert.Equal(6, figures.RunCount);
        Assert.Equal(1, figures.ExcludedRunCount);
        Assert.Equal(10m, figures.TypicalIskPerHour);
        Assert.Equal(1000m, figures.Best?.YieldIsk);
    }

    /// <summary>Loot and a homefront's ore are valued at the price handed in, not at any price fixed when they were
    /// captured: 1,000,000 fixed plus 10 × 100 gained less 1 × 50 lost plus 100 × 2 mined, and the type without a
    /// price counts 0 and is reported.</summary>
    [Fact]
    public void Compute_ValuesLootAndOreAtTodaysPrice_AndReportsAnUnpricedType()
    {
        Dictionary<int, decimal> today = new() { [34] = 100m, [36] = 50m, [1230] = 2m };
        EarningsSample sample = _Sample("pilot", 1_000_000m) with
        {
            LootGained = [new EarningsLootLine(34, 10), new EarningsLootLine(35, 5)],
            LootLost = [new EarningsLootLine(36, 1)],
            Mined = [new EarningsLootLine(1230, 100)]
        };

        EarningsFiguresDto figures = EarningsStatistics.Compute([sample],
            typeId => today.TryGetValue(typeId, out decimal price) ? price : null, AnyCount, NowUtc);

        Assert.Equal(1_001_150m, figures.Typical);
        Assert.Equal(1, figures.UnpricedItemCount);
    }

    /// <summary>Jithran 18-10: a run whose loot was never counted is left out of a site's or an abyssal's figures, where it would read
    /// as a run that brought nothing, and kept for a mission's, where the reward carries the value. Reported either
    /// way. Five runs of 10 and six uncounted runs of 0: left out, the typical run is 10; counted, it is 0.</summary>
    [Theory]
    [InlineData(RunsWithoutLoot.Excluded, 5, 10)]
    [InlineData(RunsWithoutLoot.Counted, 11, 0)]
    public void Compute_RunsWithoutCountedLoot_AreLeftOutOnlyWhenThePolicySaysSo(RunsWithoutLoot rule, int runCount,
        double typical)
    {
        EarningsSample[] samples =
        [
            .. Enumerable.Range(0, 5).Select(index => _Sample($"pilot-{index}", 10m)),
            .. Enumerable.Range(0, 6).Select(index => _Sample($"unpasted-{index}", 0m) with { LootBasis = LootBasis.None })
        ];

        EarningsFiguresDto figures = EarningsStatistics.Compute(samples, _ => null,
            EarningsPolicy.Default with { RunsWithoutLoot = rule }, NowUtc);

        Assert.Equal(runCount, figures.RunCount);
        Assert.Equal(6, figures.WithoutLootCount);
        Assert.Equal((decimal)typical, figures.Typical);
    }

    [Theory]
    [InlineData(ActivityStatsKind.Site, RunsWithoutLoot.Excluded)]
    [InlineData(ActivityStatsKind.Mission, RunsWithoutLoot.Counted)]
    [InlineData(ActivityStatsKind.Abyssal, RunsWithoutLoot.Excluded)]
    [InlineData(ActivityStatsKind.Homefront, RunsWithoutLoot.Counted)]
    public void PolicyFor_SitesAndAbyssalsLeaveTheirRunsWithoutCountedLootOut(ActivityStatsKind kind, RunsWithoutLoot rule) =>
        Assert.Equal(rule, EarningsPolicy.For(kind).RunsWithoutLoot);

    private static EarningsSample _Sample(string pilot, decimal fixedIsk, DateTime? completedAtUtc = null) => new()
    {
        PilotBucket = pilot,
        CompletedAtUtc = completedAtUtc ?? NowUtc.AddDays(-1),
        DurationSeconds = 600,
        FixedIsk = fixedIsk,
        LootBasis = LootBasis.CargoDifference
    };

    private static bool _SameRun(EarningsRunDto? expected, EarningsRunDto? actual) =>
        expected?.CompletedAtUtc == actual?.CompletedAtUtc && expected?.YieldIsk == actual?.YieldIsk
        && expected?.DurationSeconds == actual?.DurationSeconds && expected?.FleetSize == actual?.FleetSize;
}
