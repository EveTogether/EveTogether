using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>The expected yield of one activity out of the own saved runs (ET-513).</summary>
public sealed class ActivityEarningsDto
{
    public required ActivityStatsKey Key { get; init; }

    /// <summary>The name the latest run was flown as, for a screen to show; never part of the key.</summary>
    public required string? SiteName { get; init; }

    public required EarningsFiguresDto Figures { get; init; }
}
