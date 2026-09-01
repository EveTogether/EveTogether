using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class Run
{
    public Guid Id { get; set; }
    public long CharacterId { get; set; }
    public string? GroupCode { get; set; }

    /// <summary>The group this run was in when it was discarded. Stamped once and never overwritten: it is the only
    /// remaining trace that these runs were flown together, since discard unlinks rather than deletes.</summary>
    public string? FormerGroupCode { get; set; }

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

    // Two facts, never one boolean: the hauler who fetched ore during the site did participate and registers loot,
    // but takes no share. Merged, "did not fly it" and "flew it unpaid" become indistinguishable afterwards.
    public bool IsParticipant { get; set; } = true;
    public bool IsPayoutEligible { get; set; }
    public string? FitContentHash { get; set; }
    public string? FitNameSnapshot { get; set; }
    public RunSyncState SyncState { get; set; }
    public DateTime? LastPushedAtUtc { get; set; }
    public int Revision { get; set; }
    /// <summary>Detach from the shared run without touching anything the pilot owns. The one place the audit stamp
    /// is written, so a discard and the arbiter's relink can never disagree about it.</summary>
    public void UnlinkFromGroup(bool recordFormerGroup)
    {
        if (recordFormerGroup && FormerGroupCode is null && GroupCode is not null)
            FormerGroupCode = GroupCode;

        GroupCode = null;
    }

    public ICollection<RunLootCapture> LootCaptures { get; } = [];
    public ICollection<RunBountyEntry> BountyEntries { get; } = [];
    public ICollection<RunEnemyObservation> EnemyObservations { get; } = [];
    public ICollection<RunParameter> Parameters { get; } = [];
}
