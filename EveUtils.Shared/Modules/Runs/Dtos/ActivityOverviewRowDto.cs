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
    IReadOnlyList<ActivityRewardDto> Rewards,
    decimal? LootIskNet,
    int EnemyTypeCount,
    bool HasEscalation);
