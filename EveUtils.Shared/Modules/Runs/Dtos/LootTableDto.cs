using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>What can drop in one activity (ET-519): per item the drop rate, the quantity, the worth and the share of
/// the loot value, from the runs whose loot was counted between a starting and an ending hold.</summary>
public sealed class LootTableDto
{
    public required ActivityStatsKey Key { get; init; }

    public required LootTableConfidence Confidence { get; init; }

    public required LootTableWindow Window { get; init; }

    public required DateTime SinceUtc { get; init; }

    /// <summary>When the last game update was, as far as the SDE tells; null without a store. Only then is
    /// <see cref="LootTableWindow.SinceUpdate"/> a window of its own.</summary>
    public DateTime? UpdateUtc { get; init; }

    /// <summary>The runs the drop rates are taken over, unweighted.</summary>
    public int RunCount { get; init; }

    public double EffectiveRunCount { get; init; }

    public int PilotCount { get; init; }

    /// <summary>Runs left out because their loot was never counted between two holds, or still has unrecognised lines:
    /// not "runs with nothing".</summary>
    public int UncountedRunCount { get; init; }

    /// <summary>Fleet runs left out in v1: a capture holds one pilot's loot, not the activity's.</summary>
    public int FleetRunCount { get; init; }

    public int UnpricedItemCount { get; init; }

    /// <summary>The sum of the table: the average loot of a run, rare drops included.</summary>
    public decimal? ExpectedLoot { get; init; }

    /// <summary>The median loot of a run: lower than <see cref="ExpectedLoot"/> when rare drops pull the average up.</summary>
    public decimal? TypicalLoot { get; init; }

    /// <summary>The average bounty and reward of a run: paid out, not a drop.</summary>
    public decimal? ExpectedFixedIsk { get; init; }

    public int RareKindCount { get; init; }

    /// <summary>In how many of the counted runs at least one rare item dropped, unweighted.</summary>
    public int RareRunCount { get; init; }

    public double RareRunShare { get; init; }

    public double RareValueShare { get; init; }

    public bool HasUpdateFlags { get; init; }

    public required IReadOnlyList<LootTableItemDto> Items { get; init; }
}
