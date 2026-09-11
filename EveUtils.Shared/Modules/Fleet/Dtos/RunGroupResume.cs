using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// One pilot's own leg of a run whose clock is per pilot (ET-243) picked back up after their own STOP (ET-250).
/// Same shape as <see cref="RunGroupStop"/>, the moment aside: a resume needs nothing a fresh leg's start would not
/// already carry, and <see cref="EveUtils.Client.Fleet.FleetRunLegs"/> reads it exactly like one — the pilot's leg
/// is "in" again from <see cref="StartedAtUtc"/>, the same test a first entry already passes.
/// </summary>
public sealed record RunGroupResume(
    long FleetId,
    ActivityKind ActivityKind,
    string GroupCode,
    DateTime StartedAtUtc);
