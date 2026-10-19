using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.RunOutcomes.Entities;

/// <summary>
/// What one activity yielded, shared for the server's earnings statistic (ET-514): the activity's key and its take, and
/// nothing that says who flew it, where or when within the day. Deliberately not the synced run, which carries the
/// system and signature and is only visible to the pilots who flew it — sharing for statistics is a different consent.
/// </summary>
public sealed class RunOutcome
{
    public long Id { get; set; }

    /// <summary>A keyed hash of the activity's group code or run id: the fleet mates who flew it together share one.</summary>
    public required string ActivityId { get; set; }

    /// <summary>A keyed hash of the sharing character, never the id itself; it only feeds the per-pilot cap and the
    /// sharer's own retract and withdraw.</summary>
    public required string ContributorBucket { get; set; }

    /// <summary><see cref="Runs.Earnings.ActivityStatsKey.Code"/>, which the reads select on.</summary>
    public required string KeyCode { get; set; }

    public ActivityStatsKind StatsKind { get; set; }
    public SiteTypeSource? SiteTypeSource { get; set; }
    public int TypeId { get; set; }
    public string? NormalizedName { get; set; }
    public int? MissionLevel { get; set; }
    public int? AbyssalTier { get; set; }
    public string? AbyssalWeather { get; set; }

    /// <summary>The UTC day only, always at midnight: the statistic's window needs no finer time. A DateTime rather than
    /// a DateOnly because the server backup format carries no DateOnly column.</summary>
    public DateTime CompletedOn { get; set; }

    /// <summary>Rounded to the minute; null when the activity has no stop.</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>Bounty, rewards and homefront payout together, as <see cref="Runs.Earnings.EarningsSample.FixedIsk"/>.</summary>
    public decimal FixedIsk { get; set; }

    public int FleetSize { get; set; }
    public LootBasis LootBasis { get; set; }
    public int OpenUnrecognisedLootCount { get; set; }

    /// <summary>The sharer's revision of the activity; a share only replaces a stored outcome with a newer one.</summary>
    public long Revision { get; set; }

    public List<RunOutcomeLootLine> LootLines { get; set; } = [];
}
