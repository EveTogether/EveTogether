using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One reward figure by kind, summed across the activity's runs. Never collapsed into a single ISK total:
/// <see cref="RunParameterKey"/> keeps growing and some of its members (LP, Evermarks) have no ISK rate to convert
/// against. Null when none of the underlying rows carried an amount (e.g. a bare <c>Escalation</c> observation).</summary>
public sealed record ActivityRewardDto(RunParameterKey ParameterKey, decimal? Amount);

/// <summary>One row of the activity overview — <c>ActivitySummary</c> read back as-is, since it already groups on
/// <c>GroupCode ?? RunId</c> ("one row per activity"). A solo run and a six-pilot fleet both land here through the
/// same shape; nothing above distinguishes them.</summary>
public sealed record ActivityOverviewRowDto(
    Guid ActivitySummaryId,
    string? GroupCode,
    Guid? RunId,
    ActivityKind ActivityKind,
    string? SiteName,
    int? SolarSystemId,
    DateTime StartedAtUtc,
    int DurationSeconds,
    int RunsIncluded,
    int ParticipantCount,
    /// <summary>Who flew it, distinct — so one row can name its crew without the reader having to open it. The
    /// summary keeps no participant list of its own; these are the member runs' own character ids.</summary>
    IReadOnlyList<long> CharacterIds,
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
    bool HasAutoSavedRun);
