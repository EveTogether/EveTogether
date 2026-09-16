using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One reward figure by kind, summed across the activity's runs. Never collapsed into a single ISK total:
/// <see cref="RunParameterKey"/> keeps growing and some of its members (LP, Evermarks) have no ISK rate to convert
/// against. Null when none of the underlying rows carried an amount (e.g. a bare <c>Escalation</c> observation).</summary>
public sealed record ActivityRewardDto(RunParameterKey ParameterKey, decimal? Amount);

/// <summary>Where one activity stands towards one server. <see cref="IsPending"/> is true while any of its runs is
/// still queued for that server. <see cref="IsOutdated"/> is true when one of its runs had its loot corrected after
/// it was published (ET-215) — that does NOT re-queue it: the server copy waits for the pilot to publish again, or for
/// the automatic publish of a fleet run (ET-245).</summary>
public sealed record ActivityServerSyncDto(string ServerAddress, bool IsPending, bool IsOutdated = false);

/// <summary>One character who flew it, with whatever their own run recorded about them at start time (ET-212). The
/// snapshot travels here so the overview's crew line can use the same snapshot-first precedence the expanded row
/// already uses (ET-247), instead of a live-roster lookup that only knows this machine's own characters.</summary>
public sealed record ActivityCrewMemberDto(long CharacterId, string? CharacterNameSnapshot);

/// <summary>One row of the activity overview — <c>ActivitySummary</c> read back as-is, since it already groups on
/// <c>GroupCode ?? RunId</c> ("one row per activity"). A solo run and a six-pilot fleet both land here through the
/// same shape; nothing above distinguishes them.</summary>
public sealed record ActivityOverviewRowDto(
    Guid ActivitySummaryId,
    string? GroupCode,
    Guid? RunId,
    ActivityKind ActivityKind,
    string? SiteName,
    // The scanner's own group text for this site (ET-226) — one of the two facts RunTypeResolver turns into the
    // TYPE this row shows, the same way the run window and the detail screen do.
    string? SignatureGroupSnapshot,
    // The dungeon id a single catalogue match resolved to at start (ET-228) — see ActivityDetailDto's own copy of
    // this field for what 0 means here.
    int SiteTypeId,
    int? SolarSystemId,
    DateTime StartedAtUtc,
    int DurationSeconds,
    int RunsIncluded,
    int ParticipantCount,
    /// <summary>Who flew it, distinct by character — so one row can name its crew without the reader having to open
    /// it. The summary keeps no participant list of its own; these are the member runs' own character ids and
    /// name snapshots.</summary>
    IReadOnlyList<ActivityCrewMemberDto> Crew,
    IReadOnlyList<ActivityRewardDto> Rewards,
    /// <summary>What the gamelog's bounty lines paid out, summed over the activity's runs. Not a member of
    /// <see cref="Rewards"/>: those are a mission's <em>stated</em> reward forms, this is money that arrived.</summary>
    decimal BountyIsk,
    decimal? LootIskNet,
    int EnemyTypeCount,
    bool HasEscalation,
    /// <summary>At least one of the activity's runs was committed by the app itself, a day after it was stopped and
    /// never finished (ET-179). Kept apart from a pilot's own save so an activity nobody stood behind cannot pass
    /// for one that somebody did.</summary>
    bool HasAutoSavedRun,
    /// <summary>The servers this activity's runs were queued for or pushed to, from the runs themselves. Empty on an
    /// activity that never left this machine.</summary>
    IReadOnlyList<ActivityServerSyncDto> ServerSyncStates,
    /// <summary>The activity's TOTAL ISK and what each source added to it (ET-256) — the summary's stored breakdown,
    /// the very one the detail screen shows, so the row and the screen it opens can never disagree.</summary>
    IskBreakdown Isk,
    /// <summary>What this machine's own characters earned of <see cref="Isk"/> — the figure the row shows and every
    /// total on the runs overview adds up (ET-296). The group's own share of a fleet belongs in the detail, not in
    /// the pilot's totals: three of his toons on a fleet of five is his three toons' ISK here.
    ///
    /// Equal to <see cref="Isk"/> on a solo activity, on one nobody else published a run for, and whenever the
    /// caller named no characters at all (<c>GetActivityOverviewQuery.OwnCharacterIds</c>).</summary>
    IskBreakdown OwnIsk,
    /// <summary>Whether any of this machine's own characters flew it at all. False is a row that is here because a
    /// server tab holds it, or because a character was taken out of the registry — its <see cref="OwnIsk"/> is empty
    /// for a reason worth saying, rather than because nothing on it could be valued.</summary>
    bool IsFlownByOwnCharacter,
    /// <summary>The fleet mates whose own runs carry a figure — everyone outside this machine's characters with a
    /// share of their own. What lets a row this pilot flew himself, but recorded nothing on, say where the money
    /// actually went instead of passing for unvalued: an abyssal duo where his mate pasted every loot capture
    /// (measured on his own store, 11 Sep 2026). Empty when the caller named no characters of its own.</summary>
    IReadOnlyList<ActivityCrewMemberDto> OtherEarners,
    /// <summary>The abyssal pocket's own stored tier and weather (ET-241), raw as <c>RunParameterKey.AbyssalFilament</c>
    /// wrote it — null on every non-abyssal activity, and on an abyssal saved before this ticket. Not a member of
    /// <see cref="Rewards"/>: it names what the run was, it is not something the pilot earned. Read into a name (e.g.
    /// "Agitated Dark") by the client, which is the only side that knows the tier's own word.</summary>
    string? AbyssalFilamentText = null);
