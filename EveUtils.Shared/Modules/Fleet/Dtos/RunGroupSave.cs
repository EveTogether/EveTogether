using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// The fleet commander saved the shared run. Carries the group code rather than a run id, for the same reason
/// <see cref="RunGroupDiscard"/> does: a member only ever acts on their <em>own</em> runs in that group.
/// </summary>
public sealed record RunGroupSave(
    long FleetId,
    ActivityKind ActivityKind,
    string GroupCode,
    DateTime SavedAtUtc);
