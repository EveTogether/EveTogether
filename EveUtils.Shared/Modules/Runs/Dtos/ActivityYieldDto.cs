using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>What one saved activity yielded at today's price, and the activity statistic it counts under (ET-517):
/// what the run drawer compares with the typical run, and where the run's position (ET-520) starts from.</summary>
public sealed class ActivityYieldDto
{
    public required ActivityStatsKey Key { get; init; }
    public required decimal YieldIsk { get; init; }

    /// <summary>When the activity was completed: whether it lies inside the window of the figures it is ranked in.</summary>
    public DateTime CompletedAtUtc { get; init; }

    /// <summary>Whether <see cref="EarningsPolicy"/> lets the activity into the figures at all (ET-520). A run it leaves
    /// out is compared with them but is no part of them.</summary>
    public bool IsCounted { get; init; }

    /// <summary>The most valuable loot the activity brought, at today's price; null when it brought none of value.</summary>
    public ActivityTopDropDto? TopDrop { get; init; }
}

/// <summary>The single most valuable drop of one activity (ET-520).</summary>
public sealed class ActivityTopDropDto
{
    public required int TypeId { get; init; }
    public required string Name { get; init; }
    public required long Quantity { get; init; }
    public required decimal ValueIsk { get; init; }
    public required LootRarityClass Rarity { get; init; }
}
