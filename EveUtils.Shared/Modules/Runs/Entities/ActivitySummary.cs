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
    public int EnemyTypeCount { get; set; }
    public bool CompletenessUnknown { get; set; }
    public DateTime ComputedAtUtc { get; set; }
    public int SourceRevisionSum { get; set; }
}
