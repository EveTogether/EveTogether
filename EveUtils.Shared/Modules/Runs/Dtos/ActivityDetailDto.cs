using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed record RunBountyEntryDto(Guid RunId, DateTime OccurredAtUtc, decimal Isk);

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
/// whichever run happens to be running right now.</summary>
public sealed record ActivityRunDetailDto(
    Guid RunId, long CharacterId, RunRole Role, bool IsPayoutEligible,
    IReadOnlyList<RunLootCaptureDto> LootCaptures);

/// <summary>One activity, fully expanded. The totals (<see cref="LootIskGained"/> etc.) are
/// <c>ActivitySummary</c>'s own — already computed excluding excluded loot captures — rather than recomputed here,
/// so the detail can never disagree with the row that led to it.</summary>
public sealed record ActivityDetailDto(
    Guid ActivitySummaryId,
    string? GroupCode,
    ActivityKind ActivityKind,
    string? SiteName,
    int? SolarSystemId,
    DateTime StartedAtUtc,
    DateTime? StoppedAtUtc,
    int DurationSeconds,
    decimal? LootIskGained,
    decimal? LootIskLost,
    decimal? LootIskNet,
    decimal BountyIsk,
    decimal ExpectedPayoutIsk,
    IReadOnlyList<ActivityRunDetailDto> Runs,
    IReadOnlyList<RunBountyEntryDto> BountyEntries,
    IReadOnlyList<RunEnemyObservationDto> EnemyObservations,
    IReadOnlyList<RunParameterDto> Parameters);
