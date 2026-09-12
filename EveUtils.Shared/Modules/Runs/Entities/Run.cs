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

    /// <summary>The most recent moment this run was known to still be on the clock (ET-254) — written every minute
    /// or so while <see cref="State"/> is <see cref="RunState.Running"/>, null otherwise. The only source
    /// <see cref="Commands.StopRunsLeftRunningCommandHandler"/> has for when a run left running by a process that
    /// quit or crashed actually ended: without it, that handler's own restart moment was the stop time, which for a
    /// client closed overnight read the run as having lasted until the next morning. Client-local, like
    /// <see cref="SyncState"/> beside it — what another process was doing moment to moment is not this one's to
    /// carry, so it never appears in <see cref="Dtos.RunWireData"/>.</summary>
    public DateTime? LastAliveAtUtc { get; set; }

    public DateTime? DeletedAtUtc { get; set; }
    public int SiteTypeId { get; set; }

    /// <summary>Which id space <see cref="SiteTypeId"/> was taken from. Site and mission ids are disjunct spaces that
    /// reuse the same numbers, so on its own the id cannot say what it points at (ET-137).</summary>
    public SiteTypeSource SiteTypeSource { get; set; }

    public string? SiteName { get; set; }
    public int? SolarSystemId { get; set; }
    public string? Signature { get; set; }

    /// <summary>How this run was looted, or null while the pilot has not said. Stored on the run and not derived:
    /// nothing else on the row can tell a site that was blitzed from one that was cleared out.</summary>
    public RunLootStrategy? LootStrategy { get; set; }

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

    /// <summary>Whether this character was in the site when the homefront completed (ET-230) — the fact the payout is
    /// per character on, decided by the fleet commander (or the pilot over a run of their own), never measured. A
    /// third fact beside the two above, not a new meaning of <see cref="IsParticipant"/>: the ET-105 hauler flew the run
    /// unpaid and was still not in the site. Null while nobody has decided, which is every run before this column
    /// and every run that is not a homefront.</summary>
    public bool? InSiteAtCompletion { get; set; }

    /// <summary>N: how many characters EVE counts for the homefront's payout — the ticked ones on
    /// <see cref="AttendanceEntries"/> plus <see cref="AttendanceNotOnRosterCount"/>. The same on every run of the group,
    /// on every member's machine, because it is the one decision copied to each. Null while undecided.</summary>
    public int? AttendanceCount { get; set; }

    /// <summary>The pilots counted in <see cref="AttendanceCount"/> who are on no roster at all — a stranger who joined
    /// in, which EVE counts and the fleet cannot list.</summary>
    public int? AttendanceNotOnRosterCount { get; set; }

    public AttendanceSource? AttendanceSource { get; set; }

    /// <summary>The character who decided — the fleet commander at the time, or the pilot.</summary>
    public long? AttendanceSetByCharacterId { get; set; }

    /// <summary>When the decision was made, on the deciding client's clock — what lets a later correction replace an
    /// earlier one, and never the other way round.</summary>
    public DateTime? AttendanceSetAtUtc { get; set; }

    /// <summary>How many characters were on the fleet's roster when the run was stopped, externals included — a
    /// snapshot beside N, never N itself (ET-230): the hauler outside the site is in the fleet and not counted. Null
    /// on a solo run and on every run before this column.</summary>
    public int? FleetSizeAtStop { get; set; }

    /// <summary>How the homefront ended, said by the pilot or the fleet commander at STOP (ET-231) — the same one who
    /// decides <see cref="AttendanceSource"/>, on the same bundled decision. Null while nobody has said, which is
    /// every run before this column and every run that is not a homefront. Not used for Abyssal Artifact Recovery:
    /// see <see cref="HomefrontCompletedWaveCount"/>.</summary>
    public HomefrontOutcome? HomefrontOutcome { get; set; }

    /// <summary>Whether <see cref="HomefrontOutcome"/> was set by the Metaliminal Meteoroid "pale shadow" gamelog line
    /// (ET-262) rather than by hand — the one hard local signal a site completed. False on every run before this
    /// column, on every non-Metaliminal homefront, and once the pilot or fleet commander picks an outcome by hand
    /// over it: the tag never survives a manual choice, even a coincidentally identical one.</summary>
    public bool HomefrontOutcomeFromGameLog { get; set; }

    /// <summary>Abyssal Artifact Recovery's own outcome (ET-231): how many of its 9 waves paid out, 0-9, instead of
    /// <see cref="HomefrontOutcome"/> — a site that fails part-way keeps whatever waves it already cleared, so
    /// completed/failed cannot say what AAR needs said. Null on every non-AAR run and on an AAR run nobody has
    /// decided this for yet.</summary>
    public int? HomefrontCompletedWaveCount { get; set; }

    /// <summary>Which entry of <c>HomefrontPayoutTable</c> this run's own expected payout was last computed against
    /// (ET-231) — CCP has changed the table three times, so two clients on two app versions must never silently
    /// disagree about the same site's figure. Set alongside <see cref="AttendanceSetAtUtc"/> whenever a figure
    /// becomes computable; null while nothing is, and on every run before this column.</summary>
    public string? HomefrontPayoutTableVersion { get; set; }

    public string? FitContentHash { get; set; }
    public string? FitNameSnapshot { get; set; }

    /// <summary>The pilot's name as it was known locally the moment this run started (ET-212) — never re-looked-up
    /// later, the same reasoning as <see cref="FitNameSnapshot"/>. Null on every run saved before this column
    /// existed, and on a run from a fleetmate whose own client had not yet learned it either; both fall back to
    /// whatever the reader can still resolve live.</summary>
    public string? CharacterNameSnapshot { get; set; }

    /// <summary>The scanner's own group text for this site — "Combat Site", "Data Site", … — captured once when the
    /// run started, never re-looked-up (ET-226, the same snapshot reasoning as <see cref="FitNameSnapshot"/> and
    /// <see cref="CharacterNameSnapshot"/>). This is a source, not the type itself:
    /// <see cref="EveUtils.Shared.Modules.Runs.RunTypeResolver"/> turns it into a <see cref="Enums.RunTypeId"/>
    /// together with <see cref="ActivityKind"/>, so a mission or an
    /// abyssal never needs this column at all. Null on a manual start, a run saved before this column existed, or
    /// one the scanner never named a group for.</summary>
    public string? SignatureGroupSnapshot { get; set; }

    public RunSyncState SyncState { get; set; }

    /// <summary>The server <see cref="SyncState"/> and <see cref="LastPushedAtUtc"/> are about, or null while the run
    /// has never been queued for one. Those two always meant "towards a server" without naming it, which with more
    /// than one coupled server left "where does this run stand" unanswerable.</summary>
    public string? SyncServerAddress { get; set; }

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
    public ICollection<RunMiningEntry> MiningEntries { get; } = [];
    public ICollection<RunAttendanceEntry> AttendanceEntries { get; } = [];
}
