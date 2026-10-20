using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One item of a loot table (ET-519). Under <see cref="LootTableConfidence.None"/> only the type and the class
/// are filled: the name of what was seen, no number.</summary>
public sealed class LootTableItemDto
{
    public required int TypeId { get; init; }

    public required LootRarityClass RarityClass { get; init; }

    public required LootTableConfidence Confidence { get; init; }

    /// <summary>True for the rare group: a catalogue class, or seen in fewer than 5 % of the runs.</summary>
    public bool IsRare => RarityClass is not LootRarityClass.Common;

    /// <summary>In how many of the counted runs it dropped, unweighted: the "seen in 9 of 12".</summary>
    public int RunCount { get; init; }

    /// <summary>Weighted share of the runs it dropped in.</summary>
    public double DropRate { get; init; }

    /// <summary>The 90 % Wilson interval of <see cref="DropRate"/> over the effective number of runs.</summary>
    public double DropRateLow { get; init; }

    public double DropRateHigh { get; init; }

    /// <summary>The weighted average quantity in the runs where it dropped.</summary>
    public double AverageQuantity { get; init; }

    public long MinimumQuantity { get; init; }

    public long MaximumQuantity { get; init; }

    public DateTime? LastSeenUtc { get; init; }

    /// <summary>Today's price per unit, null when there is none: the item then counts as 0 and says so.</summary>
    public decimal? UnitPrice { get; init; }

    /// <summary>Average quantity times today's price: what a drop of it is worth.</summary>
    public decimal ValuePerDrop { get; init; }

    /// <summary>Drop rate times <see cref="ValuePerDrop"/>: what it adds to the expected loot of a run.</summary>
    public decimal Contribution { get; init; }

    /// <summary>Its part of the expected loot value, 0-1.</summary>
    public double ValueShare { get; init; }

    public LootItemChange Change { get; init; }
}
