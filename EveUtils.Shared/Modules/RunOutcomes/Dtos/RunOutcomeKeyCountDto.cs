using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Shared.Modules.RunOutcomes.Dtos;

/// <summary>An activity with shared outcomes in a window and how many, for the runs overview (ET-517).</summary>
public sealed class RunOutcomeKeyCountDto
{
    public required ActivityStatsKey Key { get; init; }
    public required int RunCount { get; init; }

    /// <summary>The sum of the per-pilot weights: what the statistic's thresholds are read against.</summary>
    public required double EffectiveRunCount { get; init; }

    public required int PilotCount { get; init; }
}
