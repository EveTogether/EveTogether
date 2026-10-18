using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>
/// What one activity yielded, as the earnings statistic reads it (ET-513): one sample per activity, a fleet's whole
/// take rather than one pilot's share. The loot stays as item lines rather than ISK, so it is valued at today's price
/// whenever the figures are made — here from the own runs, later from the outcomes a server hands back (ET-514).
/// </summary>
public sealed record EarningsSample
{
    /// <summary>Who flew it as far as the per-pilot cap goes: the character locally, the server's anonymous bucket over
    /// the wire. Never shown.</summary>
    public required string PilotBucket { get; init; }

    public required DateTime CompletedAtUtc { get; init; }

    /// <summary>Null when the activity has no stop: it still counts per run, never per hour.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>Bounty, rewards and homefront payout: ISK that is the same whenever it is read. Consumables and losses
    /// are the pilot's, not the site's, and are never in here.</summary>
    public required decimal FixedIsk { get; init; }

    /// <summary>Counted loot gained, one line per type.</summary>
    public IReadOnlyList<EarningsLootLine> LootGained { get; init; } = [];

    /// <summary>Counted loot lost, one line per type: what left the hold that was not a charge fired.</summary>
    public IReadOnlyList<EarningsLootLine> LootLost { get; init; } = [];

    public LootBasis LootBasis { get; init; }

    /// <summary>Pasted lines no SDE type carries yet (ET-460): their value is missing from the loot.</summary>
    public int OpenUnrecognisedLootCount { get; init; }

    public int FleetSize { get; init; } = 1;
}
