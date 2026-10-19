using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>
/// What an activity can be expected to yield (ET-513), each figure answering its own question: <see cref="Typical"/>
/// what one run brings, the band how far runs stray from it, <see cref="LongRunAverage"/> what a pilot makes over time
/// including the rare drops. Every ISK figure is gross yield, the loot at today's price. Below the minimum every
/// figure is null and only the counts are there.
/// </summary>
public sealed class EarningsFiguresDto
{
    public required EarningsConfidence Confidence { get; init; }
    public required EarningsWindow Window { get; init; }

    /// <summary>The start of the window the figures were taken over.</summary>
    public required DateTime SinceUtc { get; init; }

    /// <summary>Activities counted, before the per-pilot weights.</summary>
    public required int RunCount { get; init; }

    /// <summary>The sum of the per-pilot weights: what every threshold is held against.</summary>
    public required double EffectiveRunCount { get; init; }

    public required int PilotCount { get; init; }

    /// <summary>Activities in the window left out for being shorter than the minimum duration.</summary>
    public required int ExcludedRunCount { get; init; }

    /// <summary>Activities in the window with no loot counted at all: left out of a site's and an abyssal's figures,
    /// counted as no loot in a mission's and a homefront's (<see cref="RunsWithoutLoot"/>, Jithran 18-10).</summary>
    public required int WithoutLootCount { get; init; }

    /// <summary>Counted activities with pasted lines no SDE type carries yet, whose loot is short by those.</summary>
    public required int IncompleteLootCount { get; init; }

    /// <summary>Item types in the counted loot without a price today, valued at 0.</summary>
    public required int UnpricedItemCount { get; init; }

    public EarningsBand Band { get; init; }

    /// <summary>The weighted median ISK per run.</summary>
    public decimal? Typical { get; init; }

    /// <summary>Only with <see cref="EarningsBand.Deciles"/>.</summary>
    public decimal? P10 { get; init; }

    public decimal? P25 { get; init; }
    public decimal? P75 { get; init; }

    /// <summary>Only with <see cref="EarningsBand.Deciles"/>.</summary>
    public decimal? P90 { get; init; }

    /// <summary>The weighted mean, given only from the long-run threshold: it moves with every jackpot.</summary>
    public decimal? LongRunAverage { get; init; }

    /// <summary>The weighted share of runs above the jackpot multiple of <see cref="Typical"/>, 0-1; null when the
    /// typical run brings nothing.</summary>
    public double? JackpotShare { get; init; }

    /// <summary>The weighted median of each timed run's own ISK per hour, never total ISK over total time.</summary>
    public decimal? TypicalIskPerHour { get; init; }

    public EarningsRunDto? Worst { get; init; }
    public EarningsRunDto? Best { get; init; }
}
