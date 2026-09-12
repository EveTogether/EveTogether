using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class ActivitySummary
{
    public Guid Id { get; set; }
    public string? GroupCode { get; set; }
    public Guid? RunId { get; set; }
    public ActivityKind ActivityKind { get; set; }
    public int SiteTypeId { get; set; }
    public string? SiteName { get; set; }

    /// <summary>The representative run's own <see cref="Run.SignatureGroupSnapshot"/> (ET-226) — carried here so the
    /// runs overview can resolve a TYPE for this row without reading every run behind it.</summary>
    public string? SignatureGroupSnapshot { get; set; }

    public int? SolarSystemId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public int DurationSeconds { get; set; }
    public int RunsIncluded { get; set; }
    public int ParticipantCount { get; set; }
    public int PayoutEligibleCount { get; set; }
    public decimal? LootIskGained { get; set; }
    public decimal? LootIskLost { get; set; }
    public decimal? LootIskNet { get; set; }
    public int LootEntriesWithoutPrice { get; set; }
    public int LootItemCount { get; set; }
    public decimal LootVolume { get; set; }
    public decimal BountyIsk { get; set; }
    public decimal ExpectedPayoutIsk { get; set; }

    /// <summary>TOTAL ISK — the sum of <see cref="IskContributions"/> (ET-256), kept as a column so "ISK today" can
    /// add it up in the database. Null when no contribution has a figure.</summary>
    public decimal? TotalIsk { get; set; }

    /// <summary>What each source added or took, as JSON (<c>StoredIskBreakdown</c>). Null on a summary built before
    /// breakdowns were stored.</summary>
    public string? IskContributions { get; set; }

    /// <summary>The <c>IskContributors.Signature</c> this row was built by; any other value is rebuilt at startup.</summary>
    public string? IskSources { get; set; }

    public int EnemyTypeCount { get; set; }
    public bool CompletenessUnknown { get; set; }
    public DateTime ComputedAtUtc { get; set; }
    public int SourceRevisionSum { get; set; }
}
