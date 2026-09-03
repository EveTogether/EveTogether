using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// The fleet commander stopped the clock on the shared run. Carries the group code rather than a run id, for the
/// same reason <see cref="RunGroupDiscard"/> does: every member stops their <em>own</em> run in that group.
///
/// STOP is a pause and not an end (Raymond, 2026-09-02), so unlike a discard this takes nothing away — the row
/// stays open, and it is only the clock on every member's window that comes to rest at the commander's moment.
/// </summary>
public sealed record RunGroupStop(
    long FleetId,
    ActivityKind ActivityKind,
    string GroupCode,
    DateTime StoppedAtUtc);
