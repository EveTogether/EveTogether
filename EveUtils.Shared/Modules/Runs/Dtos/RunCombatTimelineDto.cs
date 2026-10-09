using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>
/// A run's stored combat (ET-467), its series decoded to one sum per second. A kind missing from
/// <see cref="Series"/> had no line in the run, which is not the same as a line of zero.
/// </summary>
public sealed record RunCombatTimelineDto(
    int Seconds,
    IReadOnlyDictionary<CombatSeriesKind, int[]> Series,
    int MaxHitOut,
    string? MaxHitOutTarget,
    int MaxHitIn,
    string? MaxHitInSource,
    int HitsOut,
    int MissesOut,
    int HitsIn,
    int MissesIn,
    IReadOnlyList<RunHitTallyDto> HitTallies);
