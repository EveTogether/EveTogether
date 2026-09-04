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

    /// <summary>
    /// When the pilot corrected this run's start or end by hand before saving it (ET-98), or null when both are as
    /// measured. The corrected moments are written over <see cref="StartedAtUtc"/> and <see cref="StoppedAtUtc"/> —
    /// they are the truer times and everything downstream should use them — so without this stamp nothing afterwards
    /// could tell a measured duration from a typed one. This project keeps that difference everywhere else.
    /// </summary>
    public DateTime? TimesCorrectedAtUtc { get; set; }

    /// <summary>When the app saved this run itself, because it had been stopped for a day without anyone finishing
    /// it (ET-179), or null when a pilot pressed SAVE. Both write <see cref="SavedAtUtc"/> and the same
    /// <see cref="RunState.Saved"/>, so without this column an activity nobody ever committed would be
    /// indistinguishable from one somebody stood behind.</summary>
    public DateTime? AutoSavedAtUtc { get; set; }

    public DateTime? DeletedAtUtc { get; set; }
    public int SiteTypeId { get; set; }

    /// <summary>Which id space <see cref="SiteTypeId"/> was taken from. Site and mission ids are disjunct spaces that
    /// reuse the same numbers, so on its own the id cannot say what it points at (ET-137).</summary>
    public SiteTypeSource SiteTypeSource { get; set; }

    public string? SiteName { get; set; }
    public int? SolarSystemId { get; set; }
    public string? Signature { get; set; }

    /// <summary>Where this run came from — stored, never derived. A clipboard run has no site name as often as a
    /// manual one has one, so nothing else on this row can stand in for it (ET-163).</summary>
    public RunOrigin Origin { get; set; }

    /// <summary>The agent who handed out the mission, and its level. Null on everything that is not a mission.</summary>
    public int? AgentId { get; set; }

    public int? MissionLevel { get; set; }
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
