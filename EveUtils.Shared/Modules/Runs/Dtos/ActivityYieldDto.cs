using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>What one saved activity yielded at today's price, and the activity statistic it counts under (ET-517):
/// what the run drawer compares with the typical run.</summary>
public sealed class ActivityYieldDto
{
    public required ActivityStatsKey Key { get; init; }
    public required decimal YieldIsk { get; init; }
}
