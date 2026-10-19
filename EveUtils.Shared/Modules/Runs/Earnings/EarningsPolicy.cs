using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>The thresholds of the earnings statistic (ET-512 decision, 18-10). Run counts are effective counts: the sum
/// of the per-pilot weights, so a farmer's 200 runs count as <see cref="RunsPerPilotCap"/>.</summary>
public sealed record EarningsPolicy
{
    public static EarningsPolicy Default { get; } = new();

    /// <summary>The policy an activity of <paramref name="kind"/> is read by: a site and an abyssal leave their runs
    /// without counted loot out, a mission and a homefront count them (Jithran 18-10).</summary>
    public static EarningsPolicy For(ActivityStatsKind kind) =>
        kind is ActivityStatsKind.Site or ActivityStatsKind.Abyssal
            ? Default with { RunsWithoutLoot = RunsWithoutLoot.Excluded }
            : Default;

    public RunsWithoutLoot RunsWithoutLoot { get; init; } = RunsWithoutLoot.Counted;

    /// <summary>Below this there is no figure at all.</summary>
    public double MinimumRuns { get; init; } = 5;

    /// <summary>From here the band is P10-P90, below it P25-P75.</summary>
    public double DecileBandRuns { get; init; } = 20;

    /// <summary>From here the long-run average including the rare drops is given.</summary>
    public double LongRunAverageRuns { get; init; } = 30;

    public int RunsPerPilotCap { get; init; } = 10;

    public TimeSpan Window { get; init; } = TimeSpan.FromDays(90);

    /// <summary>Taken instead of <see cref="Window"/> when that holds fewer than <see cref="MinimumRuns"/>; null for
    /// no fallback.</summary>
    public TimeSpan? FallbackWindow { get; init; } = TimeSpan.FromDays(365);

    /// <summary>A run shorter than this was given up on rather than flown; it is counted as excluded, not as a
    /// zero-ISK run. A run without a stop has no duration and is never excluded for it.</summary>
    public TimeSpan MinimumDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A run above this many times the typical one counts towards the jackpot share.</summary>
    public decimal JackpotMultiple { get; init; } = 3;
}
