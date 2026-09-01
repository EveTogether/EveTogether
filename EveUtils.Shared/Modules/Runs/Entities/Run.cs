using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class Run
{
    public Guid Id { get; set; }
    public long CharacterId { get; set; }
    public string? GroupCode { get; set; }
    public ActivityKind ActivityKind { get; set; }
    public RunState State { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public DateTime? SavedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public int SiteTypeId { get; set; }
    public string? SiteName { get; set; }
    public int? SolarSystemId { get; set; }
    public string? Signature { get; set; }
    public RunRole Role { get; set; }
    public bool IsPayoutEligible { get; set; }
    public string? FitContentHash { get; set; }
    public string? FitNameSnapshot { get; set; }
    public RunSyncState SyncState { get; set; }
    public DateTime? LastPushedAtUtc { get; set; }
    public int Revision { get; set; }
    public ICollection<RunLootCapture> LootCaptures { get; } = [];
    public ICollection<RunBountyEntry> BountyEntries { get; } = [];
    public ICollection<RunEnemyObservation> EnemyObservations { get; } = [];
    public ICollection<RunParameter> Parameters { get; } = [];
}
