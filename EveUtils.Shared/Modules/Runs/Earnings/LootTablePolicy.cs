namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>The thresholds of the loot table (ET-519, Jithran 18-10). The run thresholds are those of the earnings
/// statistic and count effective runs, so a farmer's 200 runs weigh as <see cref="RunsPerPilotCap"/>.</summary>
public sealed record LootTablePolicy
{
    public static LootTablePolicy Default { get; } = new();

    /// <summary>Below this there is no table, only the names of what was seen.</summary>
    public double MinimumRuns { get; init; } = EarningsPolicy.Default.MinimumRuns;

    /// <summary>From here a drop rate is a percentage.</summary>
    public double RatedRuns { get; init; } = EarningsPolicy.Default.DecileBandRuns;

    /// <summary>An item that dropped in fewer runs than this stays "seen in 2 of 42", however many runs there are.</summary>
    public int MinimumSightings { get; init; } = 3;

    /// <summary>With <see cref="RatedRuns"/> runs, an item seen in a smaller share of them is rare by frequency.</summary>
    public double RareDropRate { get; init; } = 0.05;

    public int RunsPerPilotCap { get; init; } = EarningsPolicy.Default.RunsPerPilotCap;

    /// <summary>The longest window, and the one the update flags are told over.</summary>
    public TimeSpan LongWindow { get; init; } = TimeSpan.FromDays(90);

    public TimeSpan ShortWindow { get; init; } = TimeSpan.FromDays(30);

    public TimeSpan MinimumDuration { get; init; } = EarningsPolicy.Default.MinimumDuration;

    /// <summary>Runs needed both before and after an update before an item may be flagged as changed.</summary>
    public int MinimumRunsPerSideOfUpdate { get; init; } = 8;

    /// <summary>"Not seen since update": the share of the earlier runs it was in at least.</summary>
    public double GoneDropRate { get; init; } = 0.10;

    /// <summary>"New since update": the share of the later runs it is in at least.</summary>
    public double NewDropRate { get; init; } = 0.15;

    /// <summary>The z of a 90 % interval.</summary>
    public double IntervalZ { get; init; } = 1.645;
}
