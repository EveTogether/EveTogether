using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// The fleet commander changed the pocket's tier or weather after the run already started (ET-241) — the same two
/// facts <see cref="RunGroupCodeStart"/> announces at START, kept in step for as long as the run runs. Carries the
/// group code rather than a run id, for the same reason <see cref="RunGroupStop"/> does: every member's window is
/// its own run under that code.
/// </summary>
public sealed record RunGroupAbyssalUpdate(
    long FleetId,
    ActivityKind ActivityKind,
    string GroupCode,
    int? TierIndex,
    string? WeatherName);
