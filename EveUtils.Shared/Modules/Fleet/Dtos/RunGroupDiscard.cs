using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// The fleet commander ended the shared run. Carries the group code rather than a run id: every member discards
/// their <em>own</em> run in that group, so no client is ever asked to reach into another pilot's data.
/// </summary>
public sealed record RunGroupDiscard(
    long FleetId,
    ActivityKind ActivityKind,
    string GroupCode,
    DateTime DiscardedAtUtc);
