using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed record RunBountyEntryDto(Guid RunId, DateTime OccurredAtUtc, decimal Isk);

/// <summary>One ore's aggregated mining on one run (ET-229) — no timestamps: the detail screen shows quantities and
/// value only (Jithran, 2026-09-11), the activity already runs from a start to a stop time.</summary>
public sealed record RunMiningEntryDto(Guid RunId, string OreType, int Units, int CriticalUnits, int ResidueUnits);

/// <summary>One sighting of one enemy type on one run. Deliberately not merged across runs by
/// <see cref="EnemyTypeId"/>: two participants in the same activity can each carry their own row for the same type,
/// with their own first/last window, and folding those into one would silently overwrite whichever sighting lost
/// the merge.</summary>
public sealed record RunEnemyObservationDto(
    Guid RunId, int EnemyTypeId, string EnemyName, int Count, DateTime FirstObservedAtUtc, DateTime LastObservedAtUtc);

public sealed record RunParameterDto(
    Guid RunId, RunParameterKey ParameterKey, string TypedValue, decimal? Amount, int? ItemTypeId,
    int? BonusWindowSeconds, DateTime ObservedAtUtc);

/// <summary>One run within the activity, with its own loot captures — never another run's, and never the loot of
/// whichever run happens to be running right now. <see cref="TimesCorrectedAtUtc"/> travels along because the
/// corrected moments are written over the start and stop themselves: without the stamp nothing downstream could
/// tell this run's duration was typed rather than measured (ET-98). <see cref="SyncState"/> travels so the screen can
/// say when a correction left a published copy behind (ET-215).</summary>
public sealed record ActivityRunDetailDto(
    Guid RunId, long CharacterId, RunRole Role, bool IsParticipant, bool IsPayoutEligible,
    DateTime StartedAtUtc, DateTime? StoppedAtUtc, DateTime? TimesCorrectedAtUtc,
    int? AgentId, int? MissionLevel, string? Signature, string? FitNameSnapshot,
    IReadOnlyList<RunLootCaptureDto> LootCaptures,
    RunSyncState SyncState = RunSyncState.Local,
    // The pilot's own name, recorded when the run started (ET-212) — null on a run saved before this column
    // existed, or one synced from a fleetmate's older client. The screen falls back to a live lookup, then to the
    // bare id, exactly as it always did when this is null.
    string? CharacterNameSnapshot = null,
    // The homefront attendance tick (ET-230) — null while nobody decided, which is every run before it.
    bool? InSiteAtCompletion = null,
    // How many characters were on the fleet's roster at STOP (ET-230) — a snapshot, never N.
    int? FleetSizeAtStop = null,
    // Which HomefrontPayoutTable entry this run's own expected figure was computed against (ET-231) — null until
    // there is one to compute. Read as-is, never recomputed on this screen: two clients on two app versions must
    // never silently disagree about the same site.
    string? HomefrontPayoutTableVersion = null);

/// <summary>One activity, fully expanded. The totals (<see cref="LootIskGained"/> etc.) are
/// <c>ActivitySummary</c>'s own — already computed excluding excluded loot captures — rather than recomputed here,
/// so the detail can never disagree with the row that led to it.</summary>
public sealed record ActivityDetailDto(
    Guid ActivitySummaryId,
    string? GroupCode,
    ActivityKind ActivityKind,
    string? SiteName,
    // The scanner's own group text for this site (ET-226) — one of the two sources RunTypeResolver turns into the
    // TYPE this screen, the run window and the runs overview all show the same way.
    string? SignatureGroupSnapshot,
    // The dungeon id a single catalogue match resolved to at start (ET-228) — 0 on every run started before this
    // ticket, on a manual mission (its own id space, SiteTypeSource.Mission), or on a site whose copied name never
    // matched exactly one dungeon. RunTypeResolver reads it together with SignatureGroupSnapshot above.
    int SiteTypeId,
    int? SolarSystemId,
    DateTime StartedAtUtc,
    DateTime? StoppedAtUtc,
    int DurationSeconds,
    decimal? LootIskGained,
    decimal? LootIskLost,
    decimal? LootIskNet,
    decimal BountyIsk,
    decimal ExpectedPayoutIsk,
    // The summary's own headcount, not Runs.Count: it counts distinct characters, and one character can hold more
    // than one run in the same activity. A screen that counted the runs instead would quietly report the wrong crew.
    int ParticipantCount,
    int PayoutEligibleCount,
    IReadOnlyList<ActivityRunDetailDto> Runs,
    IReadOnlyList<RunBountyEntryDto> BountyEntries,
    IReadOnlyList<RunEnemyObservationDto> EnemyObservations,
    IReadOnlyList<RunParameterDto> Parameters,
    IReadOnlyList<RunMiningEntryDto> MiningEntries,
    // TOTAL ISK and each source's share of it (ET-256), stored with the summary like the loot figures above.
    IskBreakdown Isk,
    // Who was in the site at completion (ET-230): the newest decision any of these runs carries, or null.
    RunAttendanceDecision? Attendance = null,
    // The fleet this activity's group was minted for (RunGroupOrigin, ET-182) — null for a solo run or a group this
    // client never recorded an origin for. What lets HOMEFRONT ask that fleet's commander and roster (ET-230).
    long? FleetId = null);
